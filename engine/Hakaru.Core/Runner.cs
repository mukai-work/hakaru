using System.Text;
using System.Text.Json;

namespace Hakaru.Core;

/// <summary>1問あたりの単価（USD / 100万トークン）。ローカル LLM は 0。</summary>
public sealed record Pricing(double InputPerMillion, double OutputPerMillion)
{
    public static readonly Pricing Free = new(0, 0);

    public double Cost(int inTok, int outTok) =>
        inTok / 1_000_000.0 * InputPerMillion + outTok / 1_000_000.0 * OutputPerMillion;
}

public static class Dataset
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static List<EvalItem> LoadJsonl(string path) =>
        File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<EvalItem>(l, Json)
                         ?? throw new InvalidDataException($"読めない行: {l}"))
            .ToList();

    /// <summary>フォルダ内の .md をファイル名順に連結する（見出しにファイル名を付ける）。</summary>
    public static string LoadDocuments(string dir)
    {
        var sb = new StringBuilder();
        foreach (var f in Directory.GetFiles(dir, "*.md").Order(StringComparer.Ordinal))
            sb.AppendLine($"## 文書: {Path.GetFileNameWithoutExtension(f)}").AppendLine(File.ReadAllText(f)).AppendLine();
        return sb.ToString();
    }
}

public sealed class EvalRunner(ILlmClient target, LlmJudge judge, Pricing pricing)
{
    public const string AnswerSystemWithContext =
        """
        あなたはソラノート株式会社のヘルプデスクAIです。下の「社内文書」に書かれていることだけを根拠に、日本語で2文以内で答えてください。
        文書に書かれていないことは推測せず、「その情報は文書に記載がありません」と答えてください。
        """;

    public const string AnswerSystemNoContext =
        "あなたはソラノート株式会社（チーム向けの議事録・メモ共有SaaS）のヘルプデスクAIです。日本語で2文以内で答えてください。";

    public async Task<RunResult> RunAsync(
        IReadOnlyList<EvalItem> items, string documents, bool withContext, string label, string datasetName,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var results = new List<ItemResult>();
        foreach (var item in items)
        {
            var system = withContext ? AnswerSystemWithContext : AnswerSystemNoContext;
            var user = withContext ? $"# 社内文書\n{documents}\n\n# 質問\n{item.Question}" : item.Question;

            var res = await target.CompleteAsync(system, user, ct);
            var (jv, _) = await judge.JudgeAsync(item, res.Text, documents, ct);
            var kw = KeywordScorer.Check(item, res.Text);
            var cost = res.ReportedCostUsd ?? pricing.Cost(res.InputTokens, res.OutputTokens);

            var r = new ItemResult(item.Id, item.Question, item.Expected, item.Answerable, res.Text,
                jv.Verdict, jv.Reason, kw, LlmJudge.Disagrees(kw, jv.Verdict),
                res.InputTokens, res.OutputTokens, Math.Round(res.LatencySeconds, 2), cost);
            results.Add(r);
            progress?.Report($"{item.Id} {r.Verdict,-13} {r.LatencySeconds,6:F1}s {(r.NeedsReview ? "要確認" : "")}");
        }

        var runId = $"{started:yyyyMMdd-HHmmss}-{Slug(label)}";
        return new RunResult(runId, label, target.ModelName, withContext, judge.ModelName, started, datasetName,
            Summarize(results), results);
    }

    public static RunSummary Summarize(IReadOnlyList<ItemResult> r)
    {
        int n = r.Count;
        int Count(Verdict v) => r.Count(x => x.Verdict == v);
        double total = r.Sum(x => x.CostUsd);
        return new RunSummary(n, Count(Verdict.Correct), Count(Verdict.Partial), Count(Verdict.Wrong),
            Count(Verdict.Hallucination), Count(Verdict.Abstained), r.Count(x => x.NeedsReview),
            n == 0 ? 0 : Math.Round((double)Count(Verdict.Correct) / n, 4),
            n == 0 ? 0 : Math.Round((double)Count(Verdict.Hallucination) / n, 4),
            Math.Round(total, 6), n == 0 ? 0 : Math.Round(total / n, 6),
            n == 0 ? 0 : Math.Round(r.Average(x => x.LatencySeconds), 2));
    }

    private static string Slug(string s) =>
        new string(s.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
}

/// <summary>人が判定を直す。AI 審判の元の判定は JudgeVerdict に残し、集計は直した後の値で出し直す。</summary>
public static class HumanReview
{
    public static RunResult Apply(RunResult run, string id, Verdict verdict, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("直した理由（note）が必要です");
        var found = false;
        var items = run.Items.Select(i =>
        {
            if (i.Id != id) return i;
            found = true;
            return i with
            {
                Verdict = verdict,
                JudgeVerdict = i.JudgeVerdict ?? i.Verdict, // 2回直しても最初の審判の判定を残す
                HumanNote = note,
                NeedsReview = false,
            };
        }).ToList();
        if (!found) throw new ArgumentException($"{id} が見つかりません");
        return run with { Items = items, Summary = EvalRunner.Summarize(items) };
    }
}

/// <summary>2回の実行を比べ、悪くなった設問を拾う（回帰検知）。</summary>
public static class Regression
{
    private static int Rank(Verdict v) => v switch
    {
        Verdict.Correct => 3,
        Verdict.Partial => 2,
        Verdict.Abstained => 1,
        _ => 0, // Wrong / Hallucination
    };

    public sealed record Change(string Id, string Question, Verdict Before, Verdict After);

    public static (List<Change> Worse, List<Change> Better) Compare(RunResult before, RunResult after)
    {
        var map = before.Items.ToDictionary(i => i.Id);
        var worse = new List<Change>();
        var better = new List<Change>();
        foreach (var a in after.Items)
        {
            if (!map.TryGetValue(a.Id, out var b)) continue;
            var c = new Change(a.Id, a.Question, b.Verdict, a.Verdict);
            if (Rank(a.Verdict) < Rank(b.Verdict)) worse.Add(c);
            else if (Rank(a.Verdict) > Rank(b.Verdict)) better.Add(c);
        }
        return (worse, better);
    }
}

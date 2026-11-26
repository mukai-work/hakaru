using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hakaru.Core;

/// <summary>
/// キーワード照合。安くて速く、判定がぶれない代わりに言い換えに弱い。
/// 各キーワードはすべて必要。「a|b」と書くとどちらか一方でよい。
/// </summary>
public static class KeywordScorer
{
    /// <summary>答えのない質問・キーワード未設定の質問は null（照合しない）。</summary>
    public static bool? Check(EvalItem item, string answer)
    {
        if (!item.Answerable || item.Keywords.Count == 0) return null;
        var a = Normalize(answer);
        return item.Keywords.All(k => k.Split('|').Any(alt => a.Contains(Normalize(alt), StringComparison.Ordinal)));
    }

    /// <summary>全角英数を半角に、空白とカンマを除き、小文字にする。</summary>
    public static string Normalize(string s)
    {
        var n = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(n.Length);
        foreach (var ch in n)
        {
            if (char.IsWhiteSpace(ch) || ch == ',') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}

/// <summary>LLM 審判の判定。</summary>
public sealed record JudgeVerdict(Verdict Verdict, string Reason);

/// <summary>
/// LLM に採点させる。温度 0・審判モデル固定で使う。
/// 審判の判定は「仮説」として扱い、キーワード照合と食い違えば人の確認に回す。
/// </summary>
public sealed partial class LlmJudge(ILlmClient judge)
{
    public string ModelName => judge.ModelName;

    public const string SystemPrompt =
        """
        あなたは社内ヘルプAIの回答を採点する審査員です。与えられた「社内文書」だけを正解の根拠にします。
        次のどれか1つで判定し、JSONだけを出力してください。説明文やコードブロックは付けないこと。
        - correct: 期待回答と同じ内容を述べている。答えが文書に無い質問で「分からない／記載がない」と答えた場合もcorrect
        - partial: 方向は合っているが、大事な条件が抜けている、または一部だけ正しい
        - wrong: 文書の内容と食い違うことを述べている
        - hallucination: 文書に無い事実（機能・数字・人名など）を、あるかのように断定している
        - abstained: 文書に答えがあるのに「分からない」と答えた
        出力形式: {"verdict":"correct|partial|wrong|hallucination|abstained","reason":"日本語で40字以内"}
        """;

    public async Task<(JudgeVerdict Verdict, LlmResponse Raw)> JudgeAsync(
        EvalItem item, string answer, string documents, CancellationToken ct = default)
    {
        var user =
            $"""
            # 社内文書
            {documents}

            # 質問
            {item.Question}

            # 期待回答
            {item.Expected}
            （この質問は文書に答えが{(item.Answerable ? "ある" : "無い")}）

            # 採点する回答
            {answer}
            """;
        var raw = await judge.CompleteAsync(SystemPrompt, user, ct);
        return (Parse(raw.Text), raw);
    }

    [GeneratedRegex("\\{.*\\}", RegexOptions.Singleline)]
    private static partial Regex JsonObject();

    /// <summary>審判の出力から判定を読み取る。読めなければ Wrong とし、理由に原文を残す。</summary>
    public static JudgeVerdict Parse(string text)
    {
        var m = JsonObject().Match(text);
        if (m.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Value);
                var v = doc.RootElement.GetProperty("verdict").GetString()?.Trim().ToLowerInvariant();
                var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                Verdict? verdict = v switch
                {
                    "correct" => Verdict.Correct,
                    "partial" => Verdict.Partial,
                    "wrong" => Verdict.Wrong,
                    "hallucination" => Verdict.Hallucination,
                    "abstained" => Verdict.Abstained,
                    _ => null,
                };
                if (verdict is { } ok) return new JudgeVerdict(ok, reason);
            }
            catch (JsonException) { }
        }
        return new JudgeVerdict(Verdict.Wrong, "審判の出力を読み取れません: " + Truncate(text, 60));
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// キーワード照合と審判が食い違うか。照合なし（null）の問いは食い違いとしない。
    /// </summary>
    public static bool Disagrees(bool? keywordHit, Verdict verdict) => keywordHit switch
    {
        true => verdict is Verdict.Wrong or Verdict.Hallucination or Verdict.Abstained,
        false => verdict is Verdict.Correct,
        null => false,
    };
}

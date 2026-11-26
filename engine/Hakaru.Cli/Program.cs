using System.Text.Json;
using Hakaru.Core;

// ハカル — AI機能の精度チェック台
//   run     評価セットを1つの構成で回し、結果JSONを書く
//   compare 2つの結果を比べ、悪くなった設問を出す（悪化があれば終了コード1）
//   index   結果フォルダの一覧 index.json を作る（画面が読む）

Console.OutputEncoding = System.Text.Encoding.UTF8;
var opts = ParseArgs(args.Skip(1).ToArray());
string Opt(string k, string? def = null) =>
    opts.TryGetValue(k, out var v) ? v : def ?? throw new ArgumentException($"--{k} が必要です");

try
{
    return args.FirstOrDefault() switch
    {
        "run" => await Run(),
        "compare" => Compare(),
        "index" => Index(),
        "review" => Review(),
        _ => Usage(),
    };
}
catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException)
{
    Console.Error.WriteLine("エラー: " + e.Message);
    return 2;
}

async Task<int> Run()
{
    var dataset = Opt("dataset");
    var docsDir = Opt("docs");
    var withContext = Opt("context", "on") == "on";
    var label = Opt("label");
    var outDir = Opt("out", "results");
    var lmUrl = Opt("lm-url", "http://127.0.0.1:1234/v1");

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    ILlmClient MakeClient(string spec) => spec.StartsWith("claude:", StringComparison.Ordinal)
        ? new ClaudeCliClient(spec["claude:".Length..])
        : new OpenAiCompatibleClient(http, lmUrl, spec, Environment.GetEnvironmentVariable("HAKARU_API_KEY"),
            opts.TryGetValue("reasoning", out var re) ? re : null);

    var target = MakeClient(Opt("model"));
    var judge = new LlmJudge(MakeClient(Opt("judge")));
    var pricing = new Pricing(double.Parse(Opt("price-in", "0")), double.Parse(Opt("price-out", "0")));

    var items = Dataset.LoadJsonl(dataset);
    if (opts.TryGetValue("limit", out var lim)) items = items.Take(int.Parse(lim)).ToList();
    var docs = Dataset.LoadDocuments(docsDir);

    Console.WriteLine($"[{label}] {target.ModelName} / 文書{(withContext ? "あり" : "なし")} / 審判 {judge.ModelName} / {items.Count}問");
    var runner = new EvalRunner(target, judge, pricing);
    var result = await runner.RunAsync(items, docs, withContext, label, Path.GetFileName(dataset),
        new Progress<string>(Console.WriteLine));

    Directory.CreateDirectory(outDir);
    var path = Path.Combine(outDir, result.RunId + ".json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, Dataset.Json));
    var s = result.Summary;
    Console.WriteLine($"正答率 {s.Accuracy:P1} / ハルシネーション率 {s.HallucinationRate:P1} / 要確認 {s.NeedsReview}問 / 費用 ${s.TotalCostUsd:F4} / 平均 {s.AvgLatencySeconds:F1}秒");
    Console.WriteLine("保存: " + Path.GetFullPath(path));
    return 0;
}

int Compare()
{
    var before = Load(Opt("before"));
    var after = Load(Opt("after"));
    var (worse, better) = Regression.Compare(before, after);
    Console.WriteLine($"{before.Label} → {after.Label}: 正答率 {before.Summary.Accuracy:P1} → {after.Summary.Accuracy:P1}");
    Console.WriteLine($"悪化 {worse.Count}問 / 改善 {better.Count}問");
    foreach (var c in worse) Console.WriteLine($"  悪化 {c.Id} {c.Before}→{c.After} {c.Question}");
    foreach (var c in better) Console.WriteLine($"  改善 {c.Id} {c.Before}→{c.After} {c.Question}");
    return worse.Count > 0 ? 1 : 0;
}

int Review()
{
    var file = Opt("file");
    if (!Enum.TryParse<Verdict>(Opt("verdict"), true, out var verdict))
        throw new ArgumentException("--verdict は Correct / Partial / Wrong / Hallucination / Abstained のどれか");
    var before = Load(file);
    var after = HumanReview.Apply(before, Opt("id"), verdict, Opt("note"));
    File.WriteAllText(file, JsonSerializer.Serialize(after, Dataset.Json));
    Console.WriteLine($"{Opt("id")} を {verdict} に直しました。正答率 {before.Summary.Accuracy:P1} → {after.Summary.Accuracy:P1}");
    return 0;
}

int Index()
{
    var dir = Opt("dir", "results");
    var runs = Directory.GetFiles(dir, "*.json")
        .Where(f => Path.GetFileName(f) != "index.json")
        .Select(f => (File: Path.GetFileName(f), Run: Load(f)))
        .OrderBy(x => x.Run.StartedAt)
        .Select(x => new
        {
            file = x.File, x.Run.RunId, x.Run.Label, x.Run.Model, x.Run.WithContext,
            x.Run.JudgeModel, x.Run.StartedAt, x.Run.Dataset, x.Run.Summary,
        })
        .ToList();
    File.WriteAllText(Path.Combine(dir, "index.json"), JsonSerializer.Serialize(runs, Dataset.Json));
    Console.WriteLine($"index.json に {runs.Count}件を書きました");
    return 0;
}

static RunResult Load(string path) =>
    JsonSerializer.Deserialize<RunResult>(File.ReadAllText(path), Dataset.Json)
    ?? throw new InvalidDataException(path);

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>();
    for (int i = 0; i < a.Length; i++)
        if (a[i].StartsWith("--", StringComparison.Ordinal))
            d[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal) ? a[++i] : "on";
    return d;
}

static int Usage()
{
    Console.WriteLine(
        """
        ハカル — AI機能の精度チェック台

          run      --dataset <jsonl> --docs <dir> --model <名前|claude:haiku> --judge <名前>
                   --label <名前> [--context on|off] [--out results] [--lm-url URL]
                   [--price-in USD/1M] [--price-out USD/1M] [--limit N]
                   [--reasoning none|low|high]   （OpenAI互換の接続先に reasoning_effort を渡す）
          compare  --before <結果.json> --after <結果.json>   （悪化があれば終了コード1）
          review   --file <結果.json> --id <問題ID> --verdict <判定> --note <直した理由>
                   （AI審判の判定を人が直す。元の判定は judgeVerdict に残る）
          index    [--dir results]
        """);
    return 0;
}

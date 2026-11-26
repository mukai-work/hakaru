using System.Text.Json.Serialization;

namespace Hakaru.Core;

/// <summary>評価セットの1問。</summary>
public sealed record EvalItem(
    string Id,
    string Question,
    string Expected,
    IReadOnlyList<string> Keywords,
    bool Answerable);

/// <summary>採点の結論。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Verdict>))]
public enum Verdict
{
    /// <summary>正しい。答えのない質問に「分からない」と答えた場合も含む。</summary>
    Correct,
    /// <summary>一部だけ正しい、または大事な条件が抜けている。</summary>
    Partial,
    /// <summary>間違っている（文書の内容と食い違う）。</summary>
    Wrong,
    /// <summary>文書に無いことを、あるかのように答えた。</summary>
    Hallucination,
    /// <summary>答えがあるのに「分からない」と答えた。</summary>
    Abstained,
}

/// <summary>LLM の1回の呼び出し結果。</summary>
public sealed record LlmResponse(
    string Text,
    int InputTokens,
    int OutputTokens,
    double LatencySeconds,
    /// <summary>呼び出し元が報告した費用（USD）。報告がなければ null で、単価表から計算する。</summary>
    double? ReportedCostUsd);

public sealed record ItemResult(
    string Id,
    string Question,
    string Expected,
    bool Answerable,
    string Answer,
    Verdict Verdict,
    string JudgeReason,
    bool? KeywordHit,
    /// <summary>キーワード照合と審判の判定が食い違う。人が見て確かめる対象。</summary>
    bool NeedsReview,
    int InputTokens,
    int OutputTokens,
    double LatencySeconds,
    double CostUsd)
{
    /// <summary>AI 審判が出した元の判定。人が直したときに残す（直していなければ null）。</summary>
    public Verdict? JudgeVerdict { get; init; }

    /// <summary>人が判定を直した理由。</summary>
    public string? HumanNote { get; init; }
}

public sealed record RunSummary(
    int Total,
    int Correct,
    int Partial,
    int Wrong,
    int Hallucination,
    int Abstained,
    int NeedsReview,
    /// <summary>正答率（Correct ÷ 全問）。</summary>
    double Accuracy,
    /// <summary>ハルシネーション率（Hallucination ÷ 全問）。</summary>
    double HallucinationRate,
    double TotalCostUsd,
    double CostPerQuestionUsd,
    double AvgLatencySeconds);

public sealed record RunResult(
    string RunId,
    string Label,
    string Model,
    bool WithContext,
    string JudgeModel,
    DateTimeOffset StartedAt,
    string Dataset,
    RunSummary Summary,
    IReadOnlyList<ItemResult> Items);

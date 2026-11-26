using Hakaru.Core;

namespace Hakaru.Tests;

public class ScoringTests
{
    private static EvalItem Item(bool answerable = true, params string[] kw) =>
        new("q", "質問", "期待", kw, answerable);

    [Fact]
    public void キーワードは全角数字と空白の違いを吸収する()
        => Assert.True(KeywordScorer.Check(Item(true, "980"), "１人あたり月額 ９８０ 円です"));

    [Fact]
    public void カンマ区切りの数字も一致する()
        => Assert.True(KeywordScorer.Check(Item(true, "1980"), "月額1,980円です"));

    [Fact]
    public void 縦棒の別表記はどれか一つでよい()
        => Assert.True(KeywordScorer.Check(Item(true, "2か月|2ヶ月"), "2ヶ月分お得です"));

    [Fact]
    public void キーワードはすべて必要()
        => Assert.False(KeywordScorer.Check(Item(true, "14日", "全額"), "14日以内なら返金されます"));

    [Fact]
    public void 答えのない質問は照合しない()
        => Assert.Null(KeywordScorer.Check(Item(false), "なんでも"));

    [Theory]
    [InlineData("{\"verdict\":\"correct\",\"reason\":\"一致\"}", Verdict.Correct)]
    [InlineData("判定です\n```json\n{\"verdict\":\"hallucination\",\"reason\":\"記載なし\"}\n```", Verdict.Hallucination)]
    [InlineData("{\"verdict\":\"ABSTAINED\"}", Verdict.Abstained)]
    public void 審判の出力を読み取れる(string text, Verdict expected)
        => Assert.Equal(expected, LlmJudge.Parse(text).Verdict);

    [Fact]
    public void 読めない審判出力はWrongにして原文を残す()
    {
        var v = LlmJudge.Parse("よく分かりません");
        Assert.Equal(Verdict.Wrong, v.Verdict);
        Assert.Contains("読み取れません", v.Reason);
    }

    [Theory]
    [InlineData(true, Verdict.Hallucination, true)]
    [InlineData(false, Verdict.Correct, true)]
    [InlineData(true, Verdict.Correct, false)]
    [InlineData(null, Verdict.Hallucination, false)]
    public void 照合と審判の食い違いを要確認にする(bool? kw, Verdict v, bool expected)
        => Assert.Equal(expected, LlmJudge.Disagrees(kw, v));

    [Fact]
    public void 思考の書き出しを取り除く()
        => Assert.Equal("答え", ThinkStripper.Strip("<think>考え中\n…</think>\n答え"));

    [Fact]
    public void 閉じタグだけ残る場合も取り除く()
        => Assert.Equal("答え", ThinkStripper.Strip("考え中</think>答え"));
}

public class RunnerTests
{
    private sealed class FakeClient(Func<string, string> reply, string name = "fake") : ILlmClient
    {
        public string ModelName => name;
        public Task<LlmResponse> CompleteAsync(string system, string user, CancellationToken ct = default)
            => Task.FromResult(new LlmResponse(reply(user), 100, 20, 0.5, null));
    }

    [Fact]
    public async Task 集計と費用計算が合う()
    {
        var items = new List<EvalItem>
        {
            new("q1", "料金は？", "980円", ["980"], true),
            new("q2", "創業者は？", "記載なし", [], false),
        };
        var target = new FakeClient(u => u.Contains("料金") ? "980円です" : "山田太郎です");
        var judge = new LlmJudge(new FakeClient(u => u.Contains("980円です")
            ? "{\"verdict\":\"correct\",\"reason\":\"一致\"}"
            : "{\"verdict\":\"hallucination\",\"reason\":\"記載なし\"}", "judge"));
        var runner = new EvalRunner(target, judge, new Pricing(1.0, 5.0));

        var r = await runner.RunAsync(items, "文書", true, "テスト", "t.jsonl");

        Assert.Equal(0.5, r.Summary.Accuracy);
        Assert.Equal(0.5, r.Summary.HallucinationRate);
        // 1問 = 100/1M*1 + 20/1M*5 = 0.0002 USD
        Assert.Equal(0.0004, r.Summary.TotalCostUsd, 6);
        Assert.Equal("judge", r.JudgeModel);
    }

    [Fact]
    public void 人が直すと元の判定を残して集計し直す()
    {
        ItemResult I(string id, Verdict v) => new(id, "q", "e", true, "a", v, "", true, true, 0, 0, 0, 0);
        var items = new[] { I("a", Verdict.Partial), I("b", Verdict.Correct) };
        var run = new RunResult("id", "l", "m", true, "j", DateTimeOffset.Now, "d", EvalRunner.Summarize(items), items);

        var once = HumanReview.Apply(run, "a", Verdict.Correct, "計算は正しい");
        var twice = HumanReview.Apply(once, "a", Verdict.Wrong, "やはり違う");

        Assert.Equal(1.0, once.Summary.Accuracy);
        var a = twice.Items[0];
        Assert.Equal(Verdict.Wrong, a.Verdict);
        Assert.Equal(Verdict.Partial, a.JudgeVerdict); // 最初の審判の判定が残る
        Assert.False(a.NeedsReview);
        Assert.Throws<ArgumentException>(() => HumanReview.Apply(run, "zzz", Verdict.Correct, "x"));
        Assert.Throws<ArgumentException>(() => HumanReview.Apply(run, "a", Verdict.Correct, " "));
    }

    [Fact]
    public void 悪化と改善を見分ける()
    {
        ItemResult I(string id, Verdict v) => new(id, "q", "e", true, "a", v, "", null, false, 0, 0, 0, 0);
        RunResult R(params ItemResult[] items) => new("id", "l", "m", true, "j", DateTimeOffset.Now, "d",
            EvalRunner.Summarize(items), items);

        var (worse, better) = Regression.Compare(
            R(I("a", Verdict.Correct), I("b", Verdict.Wrong), I("c", Verdict.Partial)),
            R(I("a", Verdict.Hallucination), I("b", Verdict.Correct), I("c", Verdict.Partial)));

        Assert.Single(worse, c => c.Id == "a");
        Assert.Single(better, c => c.Id == "b");
    }
}

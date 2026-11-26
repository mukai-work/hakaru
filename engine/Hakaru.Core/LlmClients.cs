using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hakaru.Core;

/// <summary>
/// LLM の接続先を差し替えるための窓口。評価の手順はこの1つだけに依存する。
/// </summary>
public interface ILlmClient
{
    /// <summary>結果ファイルに残す名前（例: qwen3.6-35b-a3b）。</summary>
    string ModelName { get; }

    Task<LlmResponse> CompleteAsync(string system, string user, CancellationToken ct = default);
}

/// <summary>思考の書き出し（&lt;think&gt;…&lt;/think&gt;）を答えから取り除く。</summary>
public static partial class ThinkStripper
{
    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    public static string Strip(string text)
    {
        var s = ThinkBlock().Replace(text, "");
        // 閉じタグだけ残るモデルがあるため、その手前を捨てる
        var close = s.IndexOf("</think>", StringComparison.Ordinal);
        if (close >= 0) s = s[(close + "</think>".Length)..];
        return s.Trim();
    }
}

/// <summary>
/// OpenAI 互換の API（LM Studio・Ollama・OpenAI など）に繋ぐ。URL を変えるだけで切り替えられる。
/// </summary>
public sealed class OpenAiCompatibleClient(
    HttpClient http, string baseUrl, string model, string? apiKey = null, string? reasoningEffort = null)
    : ILlmClient
{
    /// <summary>思考の強さを結果に残せるよう、指定があればモデル名に付ける。</summary>
    public string ModelName => reasoningEffort is null ? model : $"{model} (reasoning:{reasoningEffort})";

    public async Task<LlmResponse> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["max_tokens"] = 2048,
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };
        // LM Studio の Qwen3 系は reasoning_effort=none で思考を省ける（実測 80秒 → 3秒）
        if (reasoningEffort is not null) body["reasoning_effort"] = reasoningEffort;
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new("Bearer", apiKey);

        var sw = Stopwatch.StartNew();
        using var res = await http.SendAsync(req, ct);
        var payload = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            // 本文を残す（LM Studio はモデルが外れたときも 400 と理由の文を返す）
            throw new HttpRequestException($"{(int)res.StatusCode}: {payload}", null, res.StatusCode);
        using var doc = JsonDocument.Parse(payload);
        sw.Stop();

        var root = doc.RootElement;
        var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        int inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inTok = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
            outTok = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
        }
        return new LlmResponse(ThinkStripper.Strip(text), inTok, outTok, sw.Elapsed.TotalSeconds, null);
    }
}

/// <summary>
/// Claude Code の <c>claude -p</c> を子プロセスで呼ぶ。API キーが無い環境での検証用。
/// 製品の中核にはしない（呼び出し回数の制限があるため）。
/// </summary>
public sealed class ClaudeCliClient(string model = "haiku", string executable = "claude") : ILlmClient
{
    public string ModelName => $"claude-{model}";

    public async Task<LlmResponse> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-p", "--output-format", "json", "--model", model,
                     "--system-prompt", system, "--tools", "" })
            psi.ArgumentList.Add(a);

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("claude を起動できません");
        await p.StandardInput.WriteAsync(user);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        sw.Stop();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"claude が失敗しました（{p.ExitCode}）: {stderr}{stdout}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var text = root.GetProperty("result").GetString() ?? "";
        double? cost = root.TryGetProperty("total_cost_usd", out var c) ? c.GetDouble() : null;
        int inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            foreach (var key in new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" })
                if (u.TryGetProperty(key, out var v)) inTok += v.GetInt32();
            if (u.TryGetProperty("output_tokens", out var o)) outTok = o.GetInt32();
        }
        return new LlmResponse(text.Trim(), inTok, outTok, sw.Elapsed.TotalSeconds, cost);
    }
}

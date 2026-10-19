using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TokenPet;

public record Quota(string Bucket, string Window, double Remaining, DateTimeOffset? ResetsAt);

public static class Usage
{
    public static string Summarize(IReadOnlyList<Quota> quotas)
    {
        if (quotas.Count == 0) return "사용량 정보 없음";
        var multipleBuckets = quotas.Select(q => q.Bucket).Distinct().Skip(1).Any();
        var summary = string.Join(" · ", quotas.Take(2).Select(q =>
            $"{(multipleBuckets ? q.Bucket + " " : "")}{(q.Window == "7일" ? "주간" : q.Window)} {q.Remaining:0.#}%"));
        return summary + (quotas.Count > 2 ? $" +{quotas.Count - 2}" : "");
    }

    public static List<Quota> Parse(JsonElement root)
    {
        var quotas = new List<Quota>();
        if (root.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object && map.EnumerateObject().Any())
        {
            foreach (var bucket in map.EnumerateObject()) AddBucket(bucket.Value, bucket.Name);
        }
        else if (root.TryGetProperty("rateLimits", out var legacy)) AddBucket(legacy, "Codex");
        return quotas;

        void AddBucket(JsonElement bucket, string name)
        {
            if (bucket.ValueKind != JsonValueKind.Object) return;
            if (bucket.TryGetProperty("limitName", out var label) && label.ValueKind == JsonValueKind.String)
                name = label.GetString() ?? name;
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
                    !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
                string duration = "사용 한도";
                if (window.TryGetProperty("windowDurationMins", out var mins) && mins.ValueKind == JsonValueKind.Number && mins.TryGetInt64(out var m) && m > 0)
                    duration = m % 1440 == 0 ? $"{m / 1440}일" : m % 60 == 0 ? $"{m / 60}시간" : $"{m}분";
                DateTimeOffset? reset = null;
                if (window.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds) && seconds is >= -62135596800 and <= 253402300799)
                    reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                quotas.Add(new(name, duration, Math.Clamp(100 - percent, 0, 100), reset));
            }
        }
    }

    public static string? FindCodex()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "@openai", "codex");
        foreach (var candidate in new[] {
            Path.Combine(npm, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"),
            Path.Combine(npm, "node_modules", "@openai", "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc", "bin", "codex.exe"),
            Path.Combine(npm, "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe") })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public static async Task<List<Quota>> ReadAsync(string executable, CancellationToken cancellation = default)
    {
        if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codex 실행 파일(codex.exe)을 선택해 주세요.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = new(executable, "app-server") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath()
        }};
        bool started = false;
        try
        {
            process.Start();
            started = true;
            // Drain diagnostics without storing account information or credentials.
            var drain = DrainAsync(process.StandardError, timeout.Token);
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "token_pet", title = "TokenPet", version = "0.1.0" } } });
            await Response(1);
            await Send(new { method = "initialized", @params = new { } });
            await Send(new { id = 2, method = "account/rateLimits/read" });
            var result = await Response(2);
            return Parse(result);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("연결 시간이 초과됐어요. 인터넷과 Codex 로그인을 확인해 주세요.");
        }
        finally
        {
            timeout.Cancel();
            try { if (started && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // The server may exit between the check and Kill.
        }

        async Task Send(object value)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
        }
        async Task<JsonElement> Response(int id)
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var message = doc.RootElement;
                if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var n) || n != id) continue;
                if (message.TryGetProperty("error", out _))
                    throw new InvalidOperationException("사용량을 읽지 못했어요. 터미널에서 codex login 후 다시 연결해 주세요.");
                if (message.TryGetProperty("result", out var value)) return value.Clone();
                throw new InvalidOperationException("Codex 응답 형식을 확인할 수 없어요.");
            }
            throw new InvalidOperationException("Codex 연결이 종료됐어요. CLI 버전과 로그인을 확인해 주세요.");
        }
    }

    static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        try { while (await reader.ReadLineAsync(token) != null) { } }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public static void SelfTest()
    {
        using var doc = JsonDocument.Parse("""
        {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":23,"windowDurationMins":300,"resetsAt":1792094400},"secondary":{"usedPercent":91,"windowDurationMins":10080}},"other":{"primary":null}},"rateLimits":{"primary":{"usedPercent":1}}}
        """);
        var parsed = Parse(doc.RootElement);
        Check(parsed.Count == 2 && parsed[0].Remaining == 77 && parsed[0].Window == "5시간" && parsed[1].Window == "7일", "multi-bucket and remaining quota");
        Check(parsed[0].ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1792094400) && parsed[1].ResetsAt == null, "optional reset");
        using var legacy = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":150},"secondary":{"usedPercent":-5}}}""");
        Check(Parse(legacy.RootElement).Select(q => q.Remaining).SequenceEqual(new double[] { 0, 100 }), "legacy and clamping");
        using var missing = JsonDocument.Parse("""{"rateLimits":{"primary":null,"secondary":{"usedPercent":null}}}""");
        Check(Parse(missing.RootElement).Count == 0, "unknown is not zero");
        using var emptyMap = JsonDocument.Parse("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":20,"resetsAt":9223372036854775807}}}""");
        Check(Parse(emptyMap.RootElement).Single().Remaining == 80 && Parse(emptyMap.RootElement).Single().ResetsAt == null, "empty-map fallback and invalid timestamp");
        Check(Summarize(new[] { new Quota("Codex", "5시간", 77, null), new Quota("Codex", "7일", 43, null) }) == "5시간 77% · 주간 43%", "compact summary");
        Check(Summarize(Array.Empty<Quota>()) == "사용량 정보 없음", "unknown compact quota");
        Check(Summarize(new[] { new Quota("A", "5시간", 0, null), new Quota("B", "7일", 100, null), new Quota("C", "1일", 50, null) }) == "A 5시간 0% · B 주간 100% +1", "multi-bucket summary");
        static void Check(bool pass, string name) { if (!pass) throw new Exception("Self-test failed: " + name); }
    }
}

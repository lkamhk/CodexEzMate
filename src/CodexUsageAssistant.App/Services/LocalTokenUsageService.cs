using System.Globalization;
using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

internal static class LocalTokenUsageService
{
    internal static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } path
        ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    internal static async Task EnrichTodayAsync(UsageData usage, CancellationToken token)
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        if (usage.DailyTokenUsage.Any(x => x.Date == today && !x.IsLocalEstimate)) return;
        try
        {
            var count = await Task.Run(() => ReadToday(CodexHome, now, token), token);
            if (count is null) return;
            usage.DailyTokenUsage.RemoveAll(x => x.Date == today);
            usage.DailyTokenUsage.Add(new DailyTokenUsage { Date = today, Tokens = count.Value, IsLocalEstimate = true });
            usage.TodayTokens = count;
            usage.TodayTokensDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException) { }
    }

    internal static long? ReadToday(string home, DateTimeOffset now, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long sum = 0;
        bool found = false;
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        foreach (var directory in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(home, directory);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                if (File.GetLastWriteTimeUtc(file) < midnight.UtcDateTime) continue;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                    var count = CountToday(ReadLines(reader), now, seen, token);
                    if (count is not null) { found = true; sum = checked(sum + count.Value); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return found ? sum : null;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    internal static long? CountToday(IEnumerable<string> lines, DateTimeOffset now, HashSet<string> seen, CancellationToken token)
    {
        long? previous = null;
        long sum = 0;
        bool found = false;
        foreach (var line in lines)
        {
            token.ThrowIfCancellationRequested();
            // Only inspect usage events; conversation text is neither stored nor emitted.
            if (!line.Contains("token_count", StringComparison.Ordinal)) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "event_msg" ||
                    !root.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("type", out var kind) || kind.GetString() != "token_count" ||
                    !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object ||
                    !info.TryGetProperty("total_token_usage", out var total) ||
                    !total.TryGetProperty("total_tokens", out var totalValue) || !totalValue.TryGetInt64(out var current) || current < 0 ||
                    !root.TryGetProperty("timestamp", out var stamp) || !stamp.TryGetDateTimeOffset(out var timestamp)) continue;
                long? last = null;
                if (info.TryGetProperty("last_token_usage", out var lastUsage) && lastUsage.ValueKind == JsonValueKind.Object &&
                    lastUsage.TryGetProperty("total_tokens", out var lastValue) && lastValue.TryGetInt64(out var lastCount) && lastCount >= 0)
                    last = lastCount;
                var delta = previous is long before && current >= before ? current - before : last;
                previous = current;
                if (timestamp.ToOffset(now.Offset).Date != now.Date || delta is null) continue;
                found = true;
                // Forked or copied histories can contain the same usage event more than once.
                var key = $"{timestamp.UtcTicks}:{current}:{last}";
                if (seen.Add(key)) sum = checked(sum + delta.Value);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        }
        return found ? sum : null;
    }
}

using System.Globalization;
using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

internal static class LocalTokenUsageService
{
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly Dictionary<string, TokenFile> Files = new(StringComparer.OrdinalIgnoreCase);
    private static string? _cacheKey;
    private static long _access;
    private readonly record struct EventKey(long Timestamp, long Current, long Last);
    private sealed class TokenFile
    {
        internal IncrementalJsonlReader Reader = new();
        internal long? Previous;
        internal List<(EventKey Key, long Delta)> Events = [];
        internal bool Found;
        internal long Access;
        internal void Reset() { Previous = null; Events.Clear(); Found = false; }
    }
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
        CacheGate.Wait(token);
        try
        {
            var cacheKey = Path.GetFullPath(home) + "|" + now.Date.ToString("O") + "|" + now.Offset;
            if (_cacheKey != cacheKey) { Files.Clear(); _cacheKey = cacheKey; }
            var seen = new HashSet<EventKey>(); var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long sum = 0; var found = false;
            var midnight = new DateTimeOffset(now.Date, now.Offset);
            foreach (var directory in new[] { "sessions", "archived_sessions" })
            {
                var root = Path.Combine(home, directory);
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    token.ThrowIfCancellationRequested();
                    if (File.GetLastWriteTimeUtc(file) < midnight.UtcDateTime) continue;
                    live.Add(file);
                    if (!Files.TryGetValue(file, out var state)) Files[file] = state = new();
                    state.Access = ++_access;
                    try
                    {
                        state.Reader.Read(file, (record, _, _) => Accumulate(record, state, now), state.Reset, token,
                            bytes => bytes.Span.IndexOf("token_count"u8) >= 0);
                        found |= state.Found;
                        foreach (var entry in state.Events) if (seen.Add(entry.Key)) sum = checked(sum + entry.Delta);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Files.Remove(file); }
                }
            }
            foreach (var old in Files.Keys.Where(x => !live.Contains(x)).ToArray()) Files.Remove(old);
            var entries = Files.Values.Sum(x => x.Events.Count);
            foreach (var pair in Files.OrderBy(x => x.Value.Access).ToArray())
            {
                if (Files.Count <= 512 && entries <= 100000) break;
                entries -= pair.Value.Events.Count; Files.Remove(pair.Key);
            }
            return found ? sum : null;
        }
        finally { CacheGate.Release(); }
    }

    private static void Accumulate(JsonElement root, TokenFile state, DateTimeOffset now)
    {
        try
        {
            if (GoalProtocol.Text(root, "type") != "event_msg") return;
            var payload = GoalProtocol.Property(root, "payload");
            if (GoalProtocol.Text(payload, "type") != "token_count") return;
            var info = GoalProtocol.Property(payload, "info");
            var total = GoalProtocol.Property(GoalProtocol.Property(info, "total_token_usage"), "total_tokens");
            var stamp = GoalProtocol.Property(root, "timestamp");
            if (total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out var current) || current < 0 ||
                stamp.ValueKind != JsonValueKind.String || !stamp.TryGetDateTimeOffset(out var timestamp)) return;
            var latest = GoalProtocol.Property(GoalProtocol.Property(info, "last_token_usage"), "total_tokens");
            long? last = latest.ValueKind == JsonValueKind.Number && latest.TryGetInt64(out var number) && number >= 0 ? number : null;
            var delta = state.Previous is long before && current >= before ? current - before : last;
            state.Previous = current;
            if (timestamp.ToOffset(now.Offset).Date != now.Date || delta is null) return;
            state.Found = true;
            state.Events.Add((new(timestamp.UtcTicks, current, last ?? -1), delta.Value));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
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

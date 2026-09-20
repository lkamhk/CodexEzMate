using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed class AppServerUsageService : IAppServerUsageReader
{
    private readonly UsageAppServerSession _session;
    public AppServerUsageService(UsageAppServerSession? session = null) => _session = session ?? UsageAppServerSession.Shared;
    internal async Task<UsageData> ReadAfterSignInAsync(string? path, CancellationToken token)
    {
        // A fresh auth read must not depend on a long-lived process's previous account state.
        await _session.InvalidateAsync(token).ConfigureAwait(false);
        await using var fresh = _session.CreateFresh();
        return await new AppServerUsageService(fresh).ReadAsync(path, token).ConfigureAwait(false);
    }
    public async Task<UsageData> ReadAsync(string? executablePath, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        try
        {
            var client = _session;
            var usage = ParseRateLimits(await client.RequestAsync(executablePath, "account/rateLimits/read", null, token), DateTimeOffset.Now);
            if (usage.Status == UsageStatus.Available)
            {
                if (string.IsNullOrWhiteSpace(usage.PlanType))
                {
                    try
                    {
                        using var planTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        planTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        var account = await client.RequestAsync(executablePath, "account/read", new { refreshToken = false }, planTimeout.Token);
                        usage.PlanType = Property(Property(account, "account"), "planType").ValueKind == JsonValueKind.String
                            ? Property(Property(account, "account"), "planType").GetString() : null;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or KeyNotFoundException) { }
                }
                using var tokenTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                tokenTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    var activity = await client.RequestAsync(executablePath, "account/usage/read", null, tokenTimeout.Token);
                    ApplyTokenUsage(usage, activity, DateTimeOffset.Now);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or KeyNotFoundException) { }
            }
            if (usage.Status == UsageStatus.Available) await LocalTokenUsageService.EnrichTodayAsync(usage, cancellationToken);
            return usage;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("App Server 讀取逾時。", "App Server request timed out.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or JsonException or ArgumentException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return Failed("無法讀取 App Server 用量；請確認 Codex 已登入 ChatGPT，並檢查 codex.exe 路徑。",
                "Cannot read App Server usage. Sign in to ChatGPT in Codex and check the codex.exe path.");
        }
    }

    private static UsageData Failed(string zh, string en) => new()
    {
        Status = UsageStatus.NetworkError, DataSource = "App Server",
        ErrorMessage = LocalizationService.Pick(zh, en)
    };

    internal static string FindExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException("A full path to codex.exe is required.");
            return path;
        }
        var bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "codex", "codex.exe");
        if (File.Exists(bundled)) return bundled;
        var desktopRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(desktopRoot))
        {
            var desktop = Directory.EnumerateDirectories(desktopRoot)
                .Select(directory => Path.Combine(directory, "codex.exe")).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (desktop is not null) return desktop;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(path)) return path;
        }
        var npmRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        if (Directory.Exists(npmRoot))
        {
            var native = Directory.EnumerateFiles(npmRoot, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (native is not null) return native;
        }
        throw new FileNotFoundException("Codex executable was not found.");
    }

    internal static UsageData ParseRateLimits(JsonElement result, DateTimeOffset now)
    {
        var usage = new UsageData { DataSource = "App Server", LastUpdatedAt = now, Status = UsageStatus.ParseFailed };
        var bucket = Property(result, "rateLimits");
        var buckets = Property(result, "rateLimitsByLimitId");
        if (Property(buckets, "codex").ValueKind == JsonValueKind.Object) bucket = Property(buckets, "codex");
        var allowed = Property(result, "ordinaryUsageAllowed");
        if (allowed.ValueKind is JsonValueKind.True or JsonValueKind.False) usage.OrdinaryUsageAllowed = allowed.GetBoolean();
        if (Property(bucket, "spendControlReached").ValueKind == JsonValueKind.True ||
            Property(bucket, "rateLimitReachedType").ValueKind == JsonValueKind.String) usage.OrdinaryUsageAllowed = false;
        var bucketId = Property(bucket, "limitId");
        if (bucketId.ValueKind == JsonValueKind.String && bucketId.GetString() != "codex") return usage;
        var plan = Property(bucket, "planType");
        usage.PlanType = plan.ValueKind == JsonValueKind.String ? plan.GetString() : null;
        var creditSnapshot = Property(bucket, "credits");
        var balance = Property(creditSnapshot, "balance");
        usage.CreditBalance = balance.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(balance.GetString()) ? balance.GetString() : null;
        var hasCredits = Property(creditSnapshot, "hasCredits");
        usage.HasCredits = hasCredits.ValueKind is JsonValueKind.True or JsonValueKind.False ? hasCredits.GetBoolean() : null;
        var unlimited = Property(creditSnapshot, "unlimited");
        usage.UnlimitedCredits = unlimited.ValueKind is JsonValueKind.True or JsonValueKind.False ? unlimited.GetBoolean() : null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            var window = Property(bucket, name);
            var percent = Property(window, "usedPercent");
            var duration = Property(window, "windowDurationMins");
            if (percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0 ||
                duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt64(out var minutes)) continue;
            var remaining = Math.Clamp(100 - used, 0, 100);
            if (minutes == 300) { usage.FiveHourRemainingPercent = remaining; usage.FiveHourResetAt = Timestamp(Property(window, "resetsAt")); }
            if (minutes == 10080) { usage.WeeklyRemainingPercent = remaining; usage.WeeklyResetAt = Timestamp(Property(window, "resetsAt")); }
        }
        var resets = Property(result, "rateLimitResetCredits");
        var count = Property(resets, "availableCount");
        if (count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var available) && available >= 0) usage.AvailableUsageResetCount = available;
        var credits = Property(resets, "credits");
        if (credits.ValueKind == JsonValueKind.Array)
            foreach (var credit in credits.EnumerateArray())
            {
                var status = Property(credit, "status");
                if (status.ValueKind != JsonValueKind.String || status.GetString() != "available") continue;
                var title = Property(credit, "title");
                usage.UsageLimitResets.Add(new UsageLimitReset
                {
                    Id = Property(credit, "id").ValueKind == JsonValueKind.String ? Property(credit, "id").GetString() : null,
                    Type = title.ValueKind == JsonValueKind.String ? title.GetString() : "Reset",
                    ExpiresAt = Timestamp(Property(credit, "expiresAt"))
                });
            }
        if (usage.FiveHourRemainingPercent.HasValue || usage.WeeklyRemainingPercent.HasValue) usage.Status = UsageStatus.Available;
        else usage.ErrorMessage = LocalizationService.Pick("App Server 未提供 5 小時／每週用量。", "App Server did not provide 5-hour or weekly usage.");
        return usage;
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    internal static void ApplyTokenUsage(UsageData usage, JsonElement result, DateTimeOffset now)
    {
        var summary = Property(result, "summary");
        usage.LifetimeTokens = NonNegativeInteger(Property(summary, "lifetimeTokens"));
        usage.CurrentStreakDays = NonNegativeInteger(Property(summary, "currentStreakDays"));
        usage.TodayTokensDate = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        usage.TodayTokens = null;
        usage.DailyTokenUsage = [];
        var buckets = Property(result, "dailyUsageBuckets");
        if (buckets.ValueKind != JsonValueKind.Array) return;
        foreach (var bucket in buckets.EnumerateArray())
        {
            var date = Property(bucket, "startDate");
            if (date.ValueKind == JsonValueKind.String &&
                DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day) &&
                NonNegativeInteger(Property(bucket, "tokens")) is long dailyTokens)
                usage.DailyTokenUsage.Add(new DailyTokenUsage { Date = day, Tokens = dailyTokens });
            if (date.ValueKind != JsonValueKind.String || date.GetString() != usage.TodayTokensDate) continue;
            var count = NonNegativeInteger(Property(bucket, "tokens"));
            if (count is null) continue;
            try { usage.TodayTokens = checked((usage.TodayTokens ?? 0) + count.Value); }
            catch (OverflowException) { usage.TodayTokens = null; return; }
        }
    }

    private static long? NonNegativeInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;

    private static DateTimeOffset? Timestamp(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

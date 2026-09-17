using System.IO;
using System.Text.Json;

namespace CodexUsageAssistant.Services;

internal sealed class ResetRedemptionService
{
    private readonly string _path;
    private readonly Func<string?, string, string?, CancellationToken, Task<string>> _send;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal ResetRedemptionService(string? path = null,
        Func<string?, string, string?, CancellationToken, Task<string>>? send = null)
    {
        _path = path ?? Path.Combine(InstallationPaths.Root, "settings", "pending-reset.json");
        _send = send ?? SendAsync;
    }

    private sealed record Attempt(string Key, string? Executable, string? CreditId = null);

    internal async Task<string> RedeemAsync(string? executable, CancellationToken token, string? creditId = null)
    {
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Also serialize separate running app instances before reading or creating an attempt.
            using var processLock = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Attempt attempt;
            if (File.Exists(_path))
            {
                attempt = JsonSerializer.Deserialize<Attempt>(await File.ReadAllTextAsync(_path, token))
                    ?? throw new InvalidOperationException("Invalid pending reset record.");
                if (!Guid.TryParse(attempt.Key, out _) || attempt.Executable != executable)
                    throw new InvalidOperationException("Pending reset must be retried with the original Codex executable setting.");
            }
            else
            {
                attempt = new Attempt(Guid.NewGuid().ToString(), executable, creditId);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Persist before sending so uncertain attempts remain idempotent across restarts.
                await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(attempt), System.Text.Encoding.UTF8, token);
            }
            var outcome = await _send(executable, attempt.Key, attempt.CreditId, token);
            if (outcome is not ("reset" or "alreadyRedeemed" or "nothingToReset" or "noCredit"))
                throw new InvalidOperationException("Unknown reset outcome.");
            File.Delete(_path);
            return outcome;
        }
        finally { Gate.Release(); }
    }

    internal static string? SelectEarliestCredit(Models.UsageData usage, DateTimeOffset now) =>
        (usage.UsageLimitResets ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Id) &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .OrderBy(x => x.ExpiresAt ?? DateTimeOffset.MaxValue).Select(x => x.Id).FirstOrDefault();

    private static async Task<string> SendAsync(string? executable, string key, string? creditId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var client = await AppServerClient.ConnectAsync(executable, timeout.Token);
        var result = await client.RequestAsync("account/rateLimitResetCredit/consume", new { idempotencyKey = key, creditId }, timeout.Token);
        return result.GetProperty("outcome").GetString() ?? "";
    }
}

using System.ComponentModel;
using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed class ConversationDiscoveryService(ISettingsService settings) : IConversationDiscoveryService
{
    public async Task<ConversationDiscoveryResult> ScanAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var options = await settings.LoadAsync(timeout.Token);
            await using var client = await AppServerClient.ConnectAsync(options?.CodexExecutablePath, timeout.Token);
            var conversations = await ReadAllPagesAsync(async (cursor, token) =>
                await client.RequestAsync("thread/list", new
                {
                    cursor, limit = 100, archived = false, modelProviders = Array.Empty<string>(),
                    sourceKinds = new[] { "cli", "vscode", "appServer" },
                    sortKey = "updated_at", useStateDbOnly = true
                }, token), timeout.Token);
            foreach (var target in conversations)
            {
                try
                {
                    var response = await client.RequestAsync("thread/goal/get", new { threadId = target.ThreadId }, timeout.Token);
                    target.GoalStatus = GoalProtocol.Text(GoalProtocol.Property(response, "goal"), "status") ?? "none";
                    target.Compatibility = target.GoalStatus == "none" ? LocalizationService.Pick("沒有 Goal", "No Goal")
                        : string.IsNullOrWhiteSpace(target.Cwd) || !Directory.Exists(target.Cwd) ? LocalizationService.Pick("工作目錄不存在", "Working directory unavailable")
                        : LocalizationService.Pick("可檢查背景接手", "Background takeover can be checked");
                }
                catch (InvalidOperationException) { target.GoalStatus = "unknown"; target.Compatibility = LocalizationService.Pick("Goal API 未提供", "Goal API unavailable"); }
            }
            return new(conversations, LocalizationService.Pick(
                $"已從 App Server 找到 {conversations.Count} 個對話；請勾選要監控的 Goal 對話。",
                $"Found {conversations.Count} conversations via App Server. Select the Goal conversations to monitor."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new([], LocalizationService.Pick("對話掃描逾時，已保留原有監控清單。", "Scan timed out; the existing monitor list was retained."), false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or JsonException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return new([], LocalizationService.Pick("App Server 對話掃描失敗；請檢查資料來源內的 codex.exe 路徑。原有監控清單已保留。",
                "App Server conversation scan failed. Check the codex.exe path in Data source. The existing monitor list was retained."), false);
        }
    }

    internal static async Task<IReadOnlyList<ConversationTarget>> ReadAllPagesAsync(
        Func<string?, CancellationToken, Task<JsonElement>> readPage, CancellationToken token)
    {
        var targets = new Dictionary<string, ConversationTarget>(StringComparer.OrdinalIgnoreCase);
        var cursors = new HashSet<string>();
        string? cursor = null;
        do
        {
            token.ThrowIfCancellationRequested();
            var page = await readPage(cursor, token);
            foreach (var row in page.GetProperty("data").EnumerateArray())
            {
                var id = row.GetProperty("id").GetString();
                if (!Guid.TryParse(id, out _)) continue;
                var source = GoalProtocol.Property(row, "source");
                if (GoalProtocol.InternalSource(source) || GoalProtocol.Property(row, "ephemeral").ValueKind == JsonValueKind.True) continue;
                var name = row.TryGetProperty("name", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null;
                var cwd = row.TryGetProperty("cwd", out var directory) && directory.ValueKind == JsonValueKind.String ? directory.GetString() : null;
                var sectionName = row.TryGetProperty("section", out var section) && section.ValueKind == JsonValueKind.Object &&
                    section.TryGetProperty("name", out var sectionTitle) ? sectionTitle.GetString() : null;
                targets[id!] = new ConversationTarget
                {
                    ThreadId = id,
                    Cwd = cwd,
                    Source = source.ValueKind == JsonValueKind.String ? source.GetString()! : "unknown",
                    ProjectName = sectionName ?? Path.GetFileName(cwd?.TrimEnd('\\', '/')) ?? "Codex",
                    Title = string.IsNullOrWhiteSpace(name) ? id! : name,
                    IdentityStatus = ConversationIdentityStatus.Available
                };
            }
            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (!string.IsNullOrEmpty(cursor) && (!cursors.Add(cursor) || cursors.Count > 1000))
                throw new InvalidOperationException("App Server returned invalid pagination.");
        } while (!string.IsNullOrEmpty(cursor));
        // Stable IDs distinguish conversations even when their visible titles match.
        return targets.Values.OrderBy(target => target.ProjectName).ThenBy(target => target.Title).ToArray();
    }

    internal static IReadOnlyList<ConversationTarget> MergeDiscoveryPages(IEnumerable<IReadOnlyList<ConversationTarget>> pages) =>
        SidebarConversationDiscoveryService.MergeDiscoveryPages(pages);

    internal static (System.Windows.Automation.ScrollPattern? Pattern, double VerticalPercent) TryGetSidebarScroll(IntPtr handle) =>
        SidebarConversationDiscoveryService.TryGetSidebarScroll(handle);
}

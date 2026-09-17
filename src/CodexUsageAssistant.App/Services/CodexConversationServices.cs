using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CodexUsageAssistant.Models;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using FlaApplication = FlaUI.Core.Application;

namespace CodexUsageAssistant.Services;

internal static class CodexDesktopLocator
{
    public static Process? FindProcess() => Process.GetProcesses()
        .Where(process => process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero)
        .Where(process => FlaUiAutomationService.IsCodexDesktopProcessName(process.ProcessName))
        .OrderByDescending(process => process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault();
}

public sealed class SidebarConversationDiscoveryService : IConversationDiscoveryService
{
    public async Task<ConversationDiscoveryResult> ScanAsync(CancellationToken cancellationToken)
    {
        var process = CodexDesktopLocator.FindProcess();
        if (process is null) return new ConversationDiscoveryResult([], "Codex 未開啟。");

        using var app = FlaApplication.Attach(process);
        using var automation = new UIA3Automation();
        var window = app.GetAllTopLevelWindows(automation).FirstOrDefault();
        if (window is null) return new ConversationDiscoveryResult([], "找不到 Codex 主視窗。");

        var pages = new List<IReadOnlyList<ConversationTarget>>();
        var originalScroll = TryGetSidebarScroll(process.MainWindowHandle);
        try
        {
            for (var page = 0; page < 50; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pages.Add(CollectConversationLists(window));
                if (originalScroll.Pattern is null || originalScroll.Pattern.Current.VerticalScrollPercent >= 99.9) break;
                var before = originalScroll.Pattern.Current.VerticalScrollPercent;
                originalScroll.Pattern.ScrollVertical(System.Windows.Automation.ScrollAmount.LargeIncrement);
                await Task.Delay(150, cancellationToken);
                if (Math.Abs(originalScroll.Pattern.Current.VerticalScrollPercent - before) < 0.01) break;
            }
        }
        finally
        {
            if (originalScroll.Pattern is not null && originalScroll.VerticalPercent >= 0)
            {
                try { originalScroll.Pattern.SetScrollPercent(System.Windows.Automation.ScrollPattern.NoScroll, originalScroll.VerticalPercent); }
                catch (System.Windows.Automation.ElementNotAvailableException) { }
            }
        }

        var unique = MergeDiscoveryPages(pages)
            .OrderBy(target => target.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(target => target.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new ConversationDiscoveryResult(unique,
            unique.Length == 0 ? "側欄未找到對話。" : $"已找到 {unique.Length} 個對話；只讀取專案與標題。 ");
    }

    internal static IReadOnlyList<ConversationTarget> MergeDiscoveryPages(IEnumerable<IReadOnlyList<ConversationTarget>> pages)
    {
        var representatives = new Dictionary<string, ConversationTarget>(StringComparer.OrdinalIgnoreCase);
        var maximumOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            foreach (var group in page.GroupBy(target => target.Key, StringComparer.OrdinalIgnoreCase))
            {
                representatives.TryAdd(group.Key, group.First());
                maximumOccurrences[group.Key] = Math.Max(maximumOccurrences.GetValueOrDefault(group.Key), group.Count());
            }
        }

        foreach (var (key, count) in maximumOccurrences.Where(pair => pair.Value > 1))
        {
            var target = representatives[key];
            target.IdentityStatus = ConversationIdentityStatus.Ambiguous;
            target.LastStatus = ConversationResumeStatus.Ambiguous;
            target.LastMessage = "同一專案有重複標題，請先在 Codex 改名。";
        }
        return representatives.Values.ToArray();
    }

    private static IReadOnlyList<ConversationTarget> CollectConversationLists(Window window)
    {
        var output = new List<ConversationTarget>();
        var lists = window.FindAllDescendants(cf => cf.ByControlType(ControlType.List));
        foreach (var list in lists)
        {
            var name = list.Name ?? string.Empty;
            var project = name.Equals("Pinned", StringComparison.OrdinalIgnoreCase)
                ? "Pinned"
                : name.StartsWith("Scheduled tasks in ", StringComparison.OrdinalIgnoreCase)
                    ? name["Scheduled tasks in ".Length..].Trim()
                    : string.Empty;
            if (string.IsNullOrWhiteSpace(project)) continue;

            foreach (var item in list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)))
            {
                var title = item.Name?.Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;
                output.Add(new ConversationTarget { ProjectName = project, Title = title });
            }
        }
        return output;
    }

    internal static (System.Windows.Automation.ScrollPattern? Pattern, double VerticalPercent) TryGetSidebarScroll(IntPtr handle)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
            var condition = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty, "Scheduled task folders");
            var sidebar = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, condition);
            if (sidebar?.TryGetCurrentPattern(System.Windows.Automation.ScrollPattern.Pattern, out var raw) == true &&
                raw is System.Windows.Automation.ScrollPattern pattern)
                return (pattern, pattern.Current.VerticalScrollPercent);
        }
        catch (System.Windows.Automation.ElementNotAvailableException) { }
        return (null, -1);
    }
}


using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using FlaApplication = FlaUI.Core.Application;

namespace CodexUsageAssistant.Services;

public sealed class FlaUiAutomationService : IAutomationService
{
    private static readonly string[] ResumeNames = ["resume", "continue", "繼續", "继续", "恢復", "恢复"];
    private readonly string _profilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodexUsageAssistant", "profiles", "default.json");
    private readonly ISettingsService _settings;

    public FlaUiAutomationService(ISettingsService settings) => _settings = settings;

    public async Task<string> ExecuteDefaultProfileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_profilePath))
        {
            await CreateExampleAsync(cancellationToken);
            return $"已建立範例設定：{_profilePath}";
        }

        AutomationProfile profile;
        try
        {
            await using var stream = File.OpenRead(_profilePath);
            profile = await JsonSerializer.DeserializeAsync<AutomationProfile>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
                ?? throw new InvalidDataException("設定檔內容為空。 ");
        }
        catch (JsonException ex) { return $"設定檔 JSON 錯誤：{ex.Message}"; }

        if (!profile.Enabled) return "自動化設定已停用。";
        if (profile.RequireConfirmation)
        {
            var result = System.Windows.MessageBox.Show($"執行「{profile.Name}」？", "確認自動化",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes) return "已取消。";
        }

        var process = Process.GetProcessesByName(profile.Target.ProcessName).FirstOrDefault();
        if (process is null) return "目標程式未開啟。";

        using var app = FlaApplication.Attach(process);
        using var automation = new UIA3Automation();
        var window = await FindWindowAsync(app, automation, profile.Target.WindowTitleContains, 10000, cancellationToken);
        if (window is null) return "找不到目標視窗。";

        foreach (var step in profile.Steps)
        {
            try { await ExecuteStepAsync(window, step, cancellationToken); }
            catch (Exception ex) when (step.ContinueOnError && ex is not OperationCanceledException) { }
            catch (Exception ex) when (ex is not OperationCanceledException) { return $"步驟 {step.Type} 失敗：{ex.Message}"; }
        }
        return "自動化完成。";
    }

    public async Task<string> ResumeCodexAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        var process = Process.GetProcesses()
            .Where(p => p.Id != Environment.ProcessId)
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .Where(p => IsCodexDesktopProcessName(p.ProcessName))
            .OrderByDescending(p => p.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (process is null) return "Codex 未開啟。";

        try
        {
            using var app = FlaApplication.Attach(process);
            using var automation = new UIA3Automation();
            var window = await FindWindowAsync(app, automation, null, 10000, cancellationToken);
            if (window is null) return "找不到 Codex 主視窗。";

            window.Focus();
            if (settings?.UseSavedResumeCoordinates == true && settings.ResumeClickX is int x && settings.ResumeClickY is int y)
            {
                var bounds = window.BoundingRectangle;
                if (!ScreenCoordinateValidator.IsInside(x, y, bounds))
                    return $"保存座標（{x}, {y}）不在偵測到的 {process.ProcessName} 視窗「{window.Title}」範圍 " +
                           $"X={bounds.Left}..{bounds.Right - 1}, Y={bounds.Top}..{bounds.Bottom - 1}；請重新抓取。";
                FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(x, y));
                return $"已按保存座標點擊 Resume（{x}, {y}）。";
            }
            var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            var resume = buttons.FirstOrDefault(IsResumeButton);
            if (resume is null)
                return "找不到可安全確認的 Resume 按鈕；為免誤按旁邊的刪除鍵，已停止操作。";

            if (!resume.IsEnabled || resume.IsOffscreen) return "Resume 按鈕目前不可用。";
            if (resume.Patterns.Invoke.IsSupported) resume.Patterns.Invoke.Pattern.Invoke();
            else resume.Click();
            return "已自動點擊 Codex Resume。";
        }
        catch (UnauthorizedAccessException) { return "Codex 權限較高，需要管理員 Helper。"; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return $"Codex Resume 失敗：{ex.Message}"; }
    }

    internal static bool IsCodexDesktopProcessName(string processName) =>
        processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("Codex", StringComparison.OrdinalIgnoreCase);

    private static bool IsResumeButton(AutomationElement element)
    {
        return IsResumeButtonIdentity(element.Name, element.AutomationId);
    }

    internal static bool IsResumeButtonIdentity(string? name, string? automationId) =>
        ResumeNames.Any(candidate =>
            (name ?? string.Empty).Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
            (automationId ?? string.Empty).Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static async Task ExecuteStepAsync(Window window, AutomationStep step, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(1, step.TimeoutMilliseconds));
        switch (step.Type.ToLowerInvariant())
        {
            case "activatewindow": window.Focus(); break;
            case "restorewindow": window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal); break;
            case "delay": await Task.Delay(Math.Max(0, step.Milliseconds ?? 0), timeout.Token); break;
            case "invoke":
            case "click":
                var element = await FindElementAsync(window, step, timeout.Token)
                    ?? throw new InvalidOperationException("找不到指定控制項。");
                if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
                else element.Click();
                break;
            case "relativeclick":
                if (step.X is null || step.Y is null) throw new InvalidDataException("relativeClick 需要 X 和 Y。");
                var rectangle = window.BoundingRectangle;
                if (step.X < 0 || step.Y < 0 || step.X >= rectangle.Width || step.Y >= rectangle.Height)
                    throw new InvalidOperationException("相對座標超出目標視窗範圍。");
                window.Focus();
                FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(
                    rectangle.Left + step.X.Value, rectangle.Top + step.Y.Value));
                break;
            default: throw new NotSupportedException($"不支援動作：{step.Type}");
        }
    }

    private static async Task<Window?> FindWindowAsync(FlaApplication app, UIA3Automation automation, string? title, int timeoutMs, CancellationToken token)
    {
        var end = Environment.TickCount64 + timeoutMs;
        do
        {
            token.ThrowIfCancellationRequested();
            var windows = app.GetAllTopLevelWindows(automation);
            var match = string.IsNullOrWhiteSpace(title) ? windows.FirstOrDefault() :
                windows.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            await Task.Delay(200, token);
        } while (Environment.TickCount64 < end);
        return null;
    }

    private static async Task<AutomationElement?> FindElementAsync(Window window, AutomationStep step, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var element = !string.IsNullOrWhiteSpace(step.AutomationId)
                ? window.FindFirstDescendant(cf => cf.ByAutomationId(step.AutomationId))
                : window.FindFirstDescendant(cf => cf.ByName(step.Name ?? string.Empty));
            if (element is not null) return element;
            await Task.Delay(200, token);
        }
    }

    private async Task CreateExampleAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
        var profile = new AutomationProfile
        {
            Id = "notepad-example", Name = "記事本範例", Enabled = false,
            Target = new TargetApplication { ProcessName = "notepad", WindowTitleContains = "記事本" },
            Steps = [new AutomationStep { Type = "activateWindow" }, new AutomationStep { Type = "delay", Milliseconds = 500 }]
        };
        await using var stream = File.Create(_profilePath);
        await JsonSerializer.SerializeAsync(stream, profile, new JsonSerializerOptions { WriteIndented = true }, token);
    }
}

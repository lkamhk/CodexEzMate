using System.ComponentModel;
using System.Drawing;
using System.IO;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.ViewModels;
using CodexUsageAssistant.Views;
using Forms = System.Windows.Forms;

namespace CodexUsageAssistant.Services;

public sealed class TrayIconService : IDisposable, ITrayNotificationService
{
    private readonly ISettingsService _settingsService;
    private readonly SessionBrowserLauncher _sessionBrowser = new();
    private Forms.NotifyIcon? _icon;
    private Icon? _appIcon;
    private Icon? _usageIcon;
    private bool _showRemainingUsage;
    private string? _iconLabel;
    private Forms.ContextMenuStrip? _menu;
    private Forms.ToolStripMenuItem? _usageItem;
    private Forms.ToolStripMenuItem? _resetItem;
    private Forms.ToolStripMenuItem? _floatingModeItem;
    private Forms.ToolStripMenuItem? _trayModeItem;
    private Forms.ToolStripMenuItem? _bothModeItem;
    private Forms.ToolStripMenuItem? _chineseItem;
    private Forms.ToolStripMenuItem? _englishItem;
    private Forms.ToolStripMenuItem? _simplifiedItem;
    private FloatingBallViewModel? _viewModel;
    private TrayDetailsWindow? _detailsWindow;
    private UsageDisplayMode _displayMode = UsageDisplayMode.Both;
    private AppLanguage _language = AppLanguage.TraditionalChinese;

    public event Action<UsageDisplayMode>? DisplayModeChanged;
    public event Action<AppLanguage>? LanguageChanged;

    public TrayIconService(ISettingsService settingsService) => _settingsService = settingsService;

    public void Initialize(TrayDetailsWindow detailsWindow, FloatingBallViewModel viewModel)
    {
        _detailsWindow = detailsWindow;
        _viewModel = viewModel;
        _language = LocalizationService.CurrentLanguage;
        _appIcon = LoadAppIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "Codex EzMate v1.21.4",
            Visible = false
        };
        BuildMenu();
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button != Forms.MouseButtons.Left) return;
            var anchor = Forms.Cursor.Position;
            RunOnUiThread(() => detailsWindow.ToggleNearTray(anchor));
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateUsageDisplay();
    }

    public void SetDisplayMode(UsageDisplayMode mode)
    {
        _displayMode = mode;
        var trayVisible = mode is UsageDisplayMode.SystemTray or UsageDisplayMode.Both;
        if (_icon is not null) _icon.Visible = trayVisible;
        if (!trayVisible) _detailsWindow?.HideDetails();
        UpdateMenuChecks();
    }

    public void SetLanguage(AppLanguage language)
    {
        _language = language;
        BuildMenu();
        UpdateUsageDisplay();
    }

    public void SetUsageInIcon(bool enabled)
    {
        _showRemainingUsage = enabled;
        _iconLabel = null;
        UpdateUsageDisplay();
    }

    private void BuildMenu()
    {
        if (_icon is null || _viewModel is null || _detailsWindow is null) return;
        var oldMenu = _menu;
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(new Forms.ToolStripMenuItem("Codex EzMate") { Enabled = false });
        _usageItem = new Forms.ToolStripMenuItem { Enabled = false };
        _resetItem = new Forms.ToolStripMenuItem { Enabled = false };
        _menu.Items.Add(_usageItem);
        _menu.Items.Add(_resetItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(T("顯示用量詳情", "Show usage details"), null, (_, _) =>
            RunOnUiThread(() => _detailsWindow.ToggleNearTray(Forms.Cursor.Position)));
        _menu.Items.Add(T("重新整理用量", "Refresh usage"), null,
            (_, _) => RunOnUiThread(() => _viewModel.RefreshCommand.Execute(null)));
        if (_viewModel.ShowDomLoginMenu)
            _menu.Items.Add(T("登入／取得額度", "Sign in / Get usage"), null,
                (_, _) => RunOnUiThread(() => _viewModel.LoginCommand.Execute(null)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(T("開啟會話瀏覽器", "Open Session Browser"), null,
            (_, _) => RunOnUiThread(OpenSessionBrowser));
        _menu.Items.Add(T("Goal 監控", "Goal monitoring"), null,
            (_, _) => RunOnUiThread(() => _viewModel.OpenGoalMonitorCommand.Execute(null)));
        _menu.Items.Add(T("Server 代管", "Server host"), null,
            (_, _) => RunOnUiThread(() => _viewModel.OpenServerHostCommand.Execute(null)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(T("快捷鍵設定", "Keyboard shortcuts"), null,
            (_, _) => RunOnUiThread(() => _viewModel.OpenHotkeySettingsCommand.Execute(null)));
        _menu.Items.Add(T("檢查更新", "Check for updates"), null,
            (_, _) => RunOnUiThread(() => _viewModel.CheckUpdatesCommand.Execute(null)));

        var modeMenu = new Forms.ToolStripMenuItem(T("顯示模式", "Display mode"));
        _floatingModeItem = new Forms.ToolStripMenuItem(T("只顯示浮游球", "Floating ball only"), null,
            async (_, _) => await ChangeDisplayModeAsync(UsageDisplayMode.FloatingBall));
        _trayModeItem = new Forms.ToolStripMenuItem(T("只顯示系統托盤", "System tray only"), null,
            async (_, _) => await ChangeDisplayModeAsync(UsageDisplayMode.SystemTray));
        _bothModeItem = new Forms.ToolStripMenuItem(T("浮游球及系統托盤", "Floating ball and system tray"), null,
            async (_, _) => await ChangeDisplayModeAsync(UsageDisplayMode.Both));
        modeMenu.DropDownItems.AddRange([_floatingModeItem, _trayModeItem, _bothModeItem]);
        _menu.Items.Add(modeMenu);

        var languageMenu = new Forms.ToolStripMenuItem(T("介面語言", "UI language"));
        _chineseItem = new Forms.ToolStripMenuItem("繁體中文", null,
            async (_, _) => await ChangeLanguageAsync(AppLanguage.TraditionalChinese));
        _englishItem = new Forms.ToolStripMenuItem("English", null,
            async (_, _) => await ChangeLanguageAsync(AppLanguage.English));
        _simplifiedItem = new Forms.ToolStripMenuItem("简体中文", null,
            async (_, _) => await ChangeLanguageAsync(AppLanguage.SimplifiedChinese));
        languageMenu.DropDownItems.AddRange([_chineseItem, _simplifiedItem, _englishItem]);
        _menu.Items.Add(languageMenu);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(T("設定", "Settings"), null,
            (_, _) => RunOnUiThread(() => _viewModel.OpenSettingsCommand.Execute(null)));
        _menu.Items.Add(T("結束", "Exit"), null,
            (_, _) => RunOnUiThread(() => _viewModel.ExitCommand.Execute(null)));
        _icon.ContextMenuStrip = _menu;
        oldMenu?.Dispose();
        UpdateMenuChecks();
    }

    private void UpdateMenuChecks()
    {
        if (_floatingModeItem is not null) _floatingModeItem.Checked = _displayMode == UsageDisplayMode.FloatingBall;
        if (_trayModeItem is not null) _trayModeItem.Checked = _displayMode == UsageDisplayMode.SystemTray;
        if (_bothModeItem is not null) _bothModeItem.Checked = _displayMode == UsageDisplayMode.Both;
        if (_chineseItem is not null) _chineseItem.Checked = _language == AppLanguage.TraditionalChinese;
        if (_englishItem is not null) _englishItem.Checked = _language == AppLanguage.English;
        if (_simplifiedItem is not null) _simplifiedItem.Checked = _language == AppLanguage.SimplifiedChinese;
    }

    public void OpenSessionBrowser()
    {
        try { _sessionBrowser.Open(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            ShowNotification(T("無法開啟會話瀏覽器", "Could not open Session Browser"), ex.Message);
        }
    }

    public bool CanCloseSessionBrowser() => _sessionBrowser.CanClose();
    public void PrepareSessionBrowserForExit(bool exiting) => _sessionBrowser.PrepareForExit(exiting);

    private static Icon LoadAppIcon()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var extracted = Icon.ExtractAssociatedIcon(executable);
            if (extracted is not null) return extracted;
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    private async Task ChangeDisplayModeAsync(UsageDisplayMode mode)
    {
        try
        {
            var settings = await _settingsService.LoadAsync(CancellationToken.None) ?? new WindowPosition();
            settings.DisplayMode = mode;
            await _settingsService.SaveAsync(settings, CancellationToken.None);
            RunOnUiThread(() => DisplayModeChanged?.Invoke(mode));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotification(T("無法保存顯示模式", "Could not save display mode"), ex.Message);
        }
    }

    private async Task ChangeLanguageAsync(AppLanguage language)
    {
        try
        {
            var settings = await _settingsService.LoadAsync(CancellationToken.None) ?? new WindowPosition();
            settings.Language = language;
            await _settingsService.SaveAsync(settings, CancellationToken.None);
            RunOnUiThread(() => LanguageChanged?.Invoke(language));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotification(T("無法保存介面語言", "Could not save UI language"), ex.Message);
        }
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FloatingBallViewModel.ShowDomLoginMenu))
        {
            BuildMenu();
            UpdateUsageDisplay();
            return;
        }
        if (e.PropertyName is nameof(FloatingBallViewModel.FiveHourPercent)
            or nameof(FloatingBallViewModel.WeeklyPercent)
            or nameof(FloatingBallViewModel.AvailableUsageResetDisplay)
            or nameof(FloatingBallViewModel.StatusMessage))
            UpdateUsageDisplay();
    }

    private void UpdateUsageDisplay()
    {
        if (_icon is null || _viewModel is null) return;
        var label = _showRemainingUsage ? TrayUsageIconRenderer.SelectLabel(_viewModel.FiveHourPercent, _viewModel.WeeklyPercent) : null;
        if (label != _iconLabel || (!_showRemainingUsage && _usageIcon is not null))
        {
            var previous = _usageIcon;
            _usageIcon = label is null ? null : TrayUsageIconRenderer.Create(label);
            _icon.Icon = _usageIcon ?? _appIcon;
            _iconLabel = label;
            previous?.Dispose();
        }
        var usage = $"5h {_viewModel.FiveHourPercent} · W {_viewModel.WeeklyPercent}";
        var reset = $"{T("可用 reset", "Available resets")}：{_viewModel.AvailableUsageResetDisplay}";
        _icon.Text = TruncateTooltip($"Codex EzMate v1.21.4 | {usage}");
        if (_usageItem is not null) _usageItem.Text = usage;
        if (_resetItem is not null) _resetItem.Text = reset;
    }

    private string T(string traditionalChinese, string english) =>
        LocalizationService.Translate(traditionalChinese, english, _language);

    internal static string TruncateTooltip(string text) => text.Length <= 63 ? text : text[..60] + "...";

    public void Dispose()
    {
        _sessionBrowser.Dispose();
        if (_icon is null) return;
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
        _menu?.Dispose();
        _menu = null;
        _appIcon?.Dispose();
        _appIcon = null;
        _usageIcon?.Dispose();
        _usageIcon = null;
        _viewModel = null;
        _detailsWindow = null;
    }

    public void ShowNotification(string title, string message) =>
        RunOnUiThread(() => _icon?.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Warning));
}

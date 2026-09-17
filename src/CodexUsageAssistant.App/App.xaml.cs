using System.Windows;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.ViewModels;
using CodexUsageAssistant.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CodexUsageAssistant;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private TrayIconService? _trayIcon;
    private CodexAppServerHost? _host;
    private IGoalResumeService? _goals;
    private GlobalHotkeyService? _hotkeys;
    private HotkeySettingsWindow? _hotkeyWindow;
    private bool _shuttingDown;
    private Mutex? _installationMutex;
    private readonly CancellationTokenSource _updateCheck = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Setup checks for this handle and asks for a normal application exit.
        _installationMutex = new Mutex(false, @"Local\CodexEzMate.Running");

        var collection = new ServiceCollection();
        collection.AddSingleton<ISettingsService, JsonSettingsService>();
        collection.AddSingleton<AppUpdateService>();
        collection.AddSingleton<CodexAppServerHost>();
        collection.AddSingleton<WebView2UsageLoginService>();
        collection.AddSingleton<IAppServerUsageReader, AppServerUsageService>();
        collection.AddSingleton<IUsageLoginService>(provider => new UsageSourceService(
            provider.GetRequiredService<ISettingsService>(), provider.GetRequiredService<IAppServerUsageReader>(),
            provider.GetRequiredService<WebView2UsageLoginService>()));
        collection.AddSingleton<IProxyTestService, ProxyTestService>();
        collection.AddSingleton<IUsageCacheService, UsageCacheService>();
        collection.AddSingleton<IAutoResumeSettingsService, AutoResumeSettingsService>();
        collection.AddSingleton<IConversationDiscoveryService, ConversationDiscoveryService>();
        collection.AddSingleton<IGoalResumeService, GoalResumeService>();
        collection.AddSingleton<FloatingBallViewModel>();
        collection.AddSingleton<FloatingBallWindow>();
        collection.AddSingleton<TrayDetailsWindow>();
        collection.AddSingleton<TrayIconService>();
        collection.AddSingleton<ITrayNotificationService>(provider => provider.GetRequiredService<TrayIconService>());
        collection.AddSingleton<IAutoResumeScheduler, AutoResumeScheduler>();
        _services = collection.BuildServiceProvider();

        var settings = _services.GetRequiredService<ISettingsService>();
        var position = await settings.LoadAsync(CancellationToken.None) ?? new Models.WindowPosition();
        LocalizationService.Apply(position.Language);
        _host = _services.GetRequiredService<CodexAppServerHost>();
        await _host.InitializeAsync();
        _goals = _services.GetRequiredService<IGoalResumeService>();
        _host.BeforeStopAsync = token => _goals.PauseAsync(token);
        var window = _services.GetRequiredService<FloatingBallWindow>();
        var detailsWindow = _services.GetRequiredService<TrayDetailsWindow>();
        var viewModel = _services.GetRequiredService<FloatingBallViewModel>();
        window.Show();
        // Restore only after WPF has created the HWND and knows the actual monitor DPI.
        window.RestorePosition(position);
        _trayIcon = _services.GetRequiredService<TrayIconService>();
        _trayIcon.Initialize(detailsWindow, viewModel);
        _hotkeys = new GlobalHotkeyService();
        viewModel.HotkeySettingsRequested += () =>
        {
            if (_hotkeyWindow is null)
            {
                _hotkeyWindow = new HotkeySettingsWindow(settings, _hotkeys);
                _hotkeyWindow.Closed += (_, _) => _hotkeyWindow = null;
            }
            _hotkeyWindow.Show();
            if (_hotkeyWindow.WindowState == WindowState.Minimized) _hotkeyWindow.WindowState = WindowState.Normal;
            _hotkeyWindow.Activate();
        };
        _hotkeys.Invoked += action =>
        {
            if (_shuttingDown) return;
            switch (action)
            {
                case Models.HotkeyAction.SessionBrowser: _trayIcon.OpenSessionBrowser(); break;
                case Models.HotkeyAction.GoalMonitor: viewModel.OpenGoalMonitorCommand.Execute(null); break;
                case Models.HotkeyAction.ServerHost: viewModel.OpenServerHostCommand.Execute(null); break;
                case Models.HotkeyAction.UsageDetails:
                    if (!detailsWindow.IsVisible) detailsWindow.ToggleNearTray(System.Windows.Forms.Cursor.Position);
                    detailsWindow.Activate(); break;
                case Models.HotkeyAction.Settings: viewModel.OpenSettingsCommand.Execute(null); break;
                case Models.HotkeyAction.Updates: viewModel.CheckUpdatesCommand.Execute(null); break;
                case Models.HotkeyAction.HotkeySettings: viewModel.OpenHotkeySettingsCommand.Execute(null); break;
            }
        };
        if (!_hotkeys.TryApply(position.HotkeysEnabled, position.Hotkeys ?? [], out var hotkeyError))
            _trayIcon.ShowNotification(LocalizationService.Pick("快捷鍵設定", "Keyboard shortcuts"), hotkeyError);
        _trayIcon.DisplayModeChanged += mode => ApplyDisplayMode(window, _trayIcon, mode);
        _trayIcon.LanguageChanged += language => ApplyLanguage(_trayIcon, viewModel, language);
        viewModel.DisplayModeChanged += mode => ApplyDisplayMode(window, _trayIcon, mode);
        viewModel.LanguageChanged += language => ApplyLanguage(_trayIcon, viewModel, language);
        viewModel.TrayUsageIconChanged += enabled => _trayIcon.SetUsageInIcon(enabled);
        _trayIcon.SetUsageInIcon(position.TrayShowRemainingUsage);
        ApplyDisplayMode(window, _trayIcon, position.DisplayMode);
        _trayIcon.SetLanguage(position.Language);
        await viewModel.InitializeAsync();
        if (_goals is GoalResumeService recovery) _ = recovery.InitializeAsync(CancellationToken.None);
        AppUpdateService.AcknowledgeUpdateStartup(e.Args);
        if (AppUpdateService.IsConfigured) _ = CheckUpdatesAtStartupAsync(viewModel);
    }

    private async Task CheckUpdatesAtStartupAsync(FloatingBallViewModel viewModel)
    {
        try
        {
            await Task.Delay(5000, _updateCheck.Token);
            var settings = await _services!.GetRequiredService<ISettingsService>().LoadAsync(_updateCheck.Token);
            if (settings?.AutoCheckUpdates == false) return;
            var update = await _services!.GetRequiredService<AppUpdateService>().CheckAsync(_updateCheck.Token);
            if (update is not null && !_shuttingDown) viewModel.ShowUpdateWindow(update);
        }
        catch (Exception) { /* A failed automatic check must not interrupt normal startup. */ }
    }

    private static void ApplyLanguage(TrayIconService trayIcon, FloatingBallViewModel viewModel, Models.AppLanguage language)
    {
        LocalizationService.Apply(language);
        viewModel.SetLanguage(language);
        trayIcon.SetLanguage(language);
    }

    private static void ApplyDisplayMode(FloatingBallWindow window, TrayIconService trayIcon, Models.UsageDisplayMode mode)
    {
        trayIcon.SetDisplayMode(mode);
        if (mode is Models.UsageDisplayMode.FloatingBall or Models.UsageDisplayMode.Both) window.Show();
        else window.Hide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _installationMutex?.Dispose();
        _hotkeys?.Dispose();
        _updateCheck.Cancel();
        _host?.StopOwnedImmediately();
        _services?.GetService<FloatingBallViewModel>()?.StopBackgroundRefresh();
        _trayIcon?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    public async Task RequestShutdownAsync()
    {
        if (_shuttingDown) return;
        if (_trayIcon?.CanCloseSessionBrowser() == false) return;
        _trayIcon?.PrepareSessionBrowserForExit(true);
        _shuttingDown = true;
        if (_goals is GoalResumeService background) background.PrepareToExit(true);
        try
        {
            if (_goals is not null)
            {
                using var pauseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (!await _goals.PauseAsync(pauseTimeout.Token)) throw new InvalidOperationException("Goal pause is unconfirmed.");
            }
            if (_host is not null) await _host.StopAsync();
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        {
            _shuttingDown = false;
            _trayIcon?.PrepareSessionBrowserForExit(false);
            if (_goals is GoalResumeService pausedWorker) pausedWorker.PrepareToExit(false);
            System.Windows.MessageBox.Show(LocalizationService.Pick("未能確認背景 Goal 已暫停。程式保持開啟，請在 Goal 視窗重新檢查後再退出。", "Background Goal pause is unconfirmed. The app remains open; check the Goal window before exiting."));
            return;
        }
        _updateCheck.Cancel();
        _services?.GetService<FloatingBallViewModel>()?.StopBackgroundRefresh();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (_trayIcon?.CanCloseSessionBrowser() == false) { e.Cancel = true; return; }
        if (_goals?.HasActiveWork == true)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                if (!_goals.PauseAsync(timeout.Token).GetAwaiter().GetResult()) { e.Cancel = true; return; }
            }
            catch { e.Cancel = true; return; }
        }
        _host?.StopOwnedImmediately();
        base.OnSessionEnding(e);
    }
}

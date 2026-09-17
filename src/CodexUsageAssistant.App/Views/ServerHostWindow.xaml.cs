using System.IO;
using System.Windows;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.Views;

public partial class ServerHostWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly CodexAppServerHost? _host;
    private WindowPosition _model = new();
    public ServerHostWindow(ISettingsService settings, CodexAppServerHost? host)
    {
        _settings = settings; _host = host;
        InitializeComponent();
        if (_host is not null)
        {
            _host.Changed += OnHostChanged;
            Closed += (_, _) => { _host.Changed -= OnHostChanged; _host.CancelLogin(); };
        }
        Loaded += OnLoaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _model = await _settings.LoadAsync(CancellationToken.None) ?? new WindowPosition();
        if (AppServerHostRuntime.FillDefaults(_model)) await _settings.SaveAsync(_model, CancellationToken.None);
        HostAutoStartCheckBox.IsChecked = _model.HostAutoStart;
        HostPathTextBox.Text = _model.HostExecutablePath ?? "";
        HostPortTextBox.Text = _model.HostPort.ToString();
        HostWorkTextBox.Text = _model.HostWorkingDirectory ?? "";
        OnHostChanged();
    }
    private async void OnSaveClick(object sender, RoutedEventArgs e) => await RunHostActionAsync(() => Task.CompletedTask, true);
    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
    private void ReadHostFields(WindowPosition model)
    {
        if (!int.TryParse(HostPortTextBox.Text, out var port) || port is < 1 or > 65535)
            throw new InvalidDataException(L("Server 端口必須為 1–65535。", "Server port must be 1–65535."));
        model.HostPort = port;
        model.HostExecutablePath = HostPathTextBox.Text.Trim().Trim('"');
        model.HostWorkingDirectory = HostWorkTextBox.Text.Trim().Trim('"');
        AppServerHostRuntime.FillDefaults(model);
        HostPathTextBox.Text = model.HostExecutablePath ?? "";
        HostWorkTextBox.Text = model.HostWorkingDirectory ?? "";
        model.HostAutoStart = HostAutoStartCheckBox.IsChecked == true;
        if (model.HostAutoStart) AppServerHostRuntime.Validate(model, false);
        _model.HostPort = port; _model.HostAutoStart = model.HostAutoStart;
        _model.HostExecutablePath = model.HostExecutablePath; _model.HostWorkingDirectory = model.HostWorkingDirectory;
    }

    private void OnHostChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            HostStatusText.Text = _host is null ? L("代管服務未載入。", "Host service not loaded.") : _host.StatusText + "\n" + _host.Details;
        }));
    }

    private async Task RunHostActionAsync(Func<Task> action, bool save = false)
    {
        if (_host is null) return;
        HostActionsPanel.IsEnabled = false;
        try
        {
            if (save)
            {
                var latest = await _settings.LoadAsync(CancellationToken.None) ?? new WindowPosition();
                ReadHostFields(latest);
                await _settings.SaveAsync(latest, CancellationToken.None);
                await _host.ApplySettingsAsync(latest);
            }
            await action();
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        {
            System.Windows.MessageBox.Show(L("未能完成操作。請檢查端口、Codex 完整路徑、空白資料夾及 Proxy；登入請使用 Codex 帳戶。", "Operation failed. Check the port, absolute Codex path, empty directory and proxy; sign in with Codex."), L("Server 代管", "Server host"));
        }
        finally { HostActionsPanel.IsEnabled = true; OnHostChanged(); }
    }
    private async void OnHostStart(object sender, RoutedEventArgs e) => await RunHostActionAsync(() => _host!.StartAsync(), true);
    private async void OnHostStop(object sender, RoutedEventArgs e) => await RunHostActionAsync(() => _host!.StopAsync());
    private async void OnHostRestart(object sender, RoutedEventArgs e)
    {
        if (_host?.OwnsServer == true && System.Windows.MessageBox.Show(L("重新啟動會中斷此 server 的進行中查詢，繼續？", "Restart interrupts active queries on this server. Continue?"), "Codex App Server", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunHostActionAsync(() => _host!.RestartAsync());
    }
    private async void OnHostCheck(object sender, RoutedEventArgs e) => await RunHostActionAsync(() => _host!.CheckEndpointAsync());
    private async void OnHostLogin(object sender, RoutedEventArgs e) => await RunHostActionAsync(() => _host!.LoginAsync(uri =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })));
    private void OnHostCancelLogin(object sender, RoutedEventArgs e) => _host?.CancelLogin();
    private async void OnHostModels(object sender, RoutedEventArgs e) => await RunHostActionAsync(async () =>
    {
        var models = await _host!.ListModelsAsync();
        System.Windows.MessageBox.Show(string.Join("\n", models), L("可用模型", "Available models"));
    });
    private void OnHostBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Codex executable|codex.exe|Executable|*.exe" };
        if (dialog.ShowDialog(this) == true) HostPathTextBox.Text = dialog.FileName;
    }

    private void OnHostDetect(object sender, RoutedEventArgs e)
    {
        try { HostPathTextBox.Text = AppServerUsageService.FindExecutable(null); }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        { System.Windows.MessageBox.Show(L("未偵測到 Codex，請使用旁邊的瀏覽按鈕選擇 codex.exe。", "Codex was not found. Use Browse to select codex.exe.")); }
    }

    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
}

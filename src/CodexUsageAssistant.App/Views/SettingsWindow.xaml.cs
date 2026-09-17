using System.IO;
using System.Windows;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.Views;

public partial class SettingsWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly IProxyTestService _proxyTester;
    private readonly CodexAppServerHost? _host;
    private WindowPosition _model = new();
    public SettingsWindow(ISettingsService settings, IProxyTestService proxyTester, CodexAppServerHost? host = null)
    {
        _settings = settings; _proxyTester = proxyTester; _host = host;
        InitializeComponent();
        Loaded += OnLoaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _model = await _settings.LoadAsync(CancellationToken.None) ?? new WindowPosition();
        TrayUsageCheckBox.IsChecked = _model.TrayShowRemainingUsage;
        AutoUpdateCheckBox.IsChecked = _model.AutoCheckUpdates;
        ServerNotificationCheckBox.IsChecked = _model.RefreshOnServerNotification;
        AppServerFallbackRadio.IsChecked = _model.UsageReadMode == UsageReadMode.AppServerWithDomFallback;
        AppServerOnlyRadio.IsChecked = _model.UsageReadMode == UsageReadMode.AppServerOnly;
        DomOnlyRadio.IsChecked = _model.UsageReadMode == UsageReadMode.DomOnly;
        CodexExecutableTextBox.Text = _model.CodexExecutablePath ?? string.Empty;
        FloatingBallModeRadio.IsChecked = _model.DisplayMode == UsageDisplayMode.FloatingBall;
        SystemTrayModeRadio.IsChecked = _model.DisplayMode == UsageDisplayMode.SystemTray;
        BothModeRadio.IsChecked = _model.DisplayMode == UsageDisplayMode.Both;
        ChineseLanguageRadio.IsChecked = _model.Language == AppLanguage.TraditionalChinese;
        EnglishLanguageRadio.IsChecked = _model.Language == AppLanguage.English;
        SimplifiedLanguageRadio.IsChecked = _model.Language == AppLanguage.SimplifiedChinese;
        ProxyEnabledCheckBox.IsChecked = _model.ProxyEnabled;
        ProxyServerTextBox.Text = _model.ProxyServer ?? string.Empty;
        BypassTextBox.Text = _model.ProxyBypassList ?? string.Empty;
        ProxyUsernameTextBox.Text = _model.ProxyUsername ?? string.Empty;
        ProxyPasswordBox.Password = CredentialProtector.Unprotect(_model.EncryptedProxyPassword) ?? string.Empty;
        AutoRefreshMinutesTextBox.Text = _model.AutoRefreshIntervalMinutes is > 0
            ? _model.AutoRefreshIntervalMinutes.Value.ToString()
            : string.Empty;
    }
    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestProgress.Visibility = Visibility.Visible;
        TestStatusText.Text = L("正在透過目前設定連接 ipinfo.io…", "Connecting to ipinfo.io with the current settings…");
        try
        {
            var proxy = ProxyEnabledCheckBox.IsChecked == true ? ProxyServerTextBox.Text : null;
            var result = await _proxyTester.TestAsync(proxy, ProxyUsernameTextBox.Text.Trim(),
                ProxyPasswordBox.Password, CancellationToken.None);
            TestStatusText.Text = $"{result.Message}，{L("耗時", "elapsed")} {result.Elapsed.TotalMilliseconds:0} ms";
            TestStatusText.Foreground = result.Success ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Salmon;
        }
        catch (InvalidDataException ex) { TestStatusText.Text = ex.Message; TestStatusText.Foreground = System.Windows.Media.Brushes.Salmon; }
        finally { TestProgress.Visibility = Visibility.Collapsed; TestButton.IsEnabled = true; }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var server = ProxyServerTextBox.Text.Trim();
            if (ProxyEnabledCheckBox.IsChecked == true) ProxyConfiguration.NormalizeAndValidate(server);
            ProxyConfiguration.BuildBrowserArguments(ProxyEnabledCheckBox.IsChecked == true ? server : null, BypassTextBox.Text.Trim());
            var refreshText = AutoRefreshMinutesTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(refreshText) && (!int.TryParse(refreshText, out var refreshMinutes) || refreshMinutes < 0))
                throw new InvalidDataException(L("自動更新頻率必須是 0 或正整數分鐘。",
                    "The auto-refresh interval must be 0 or a positive whole number of minutes."));
            _model = await _settings.LoadAsync(CancellationToken.None) ?? new WindowPosition();
            _model.TrayShowRemainingUsage = TrayUsageCheckBox.IsChecked == true;
            _model.AutoCheckUpdates = AutoUpdateCheckBox.IsChecked == true;
            _model.ProxyEnabled = ProxyEnabledCheckBox.IsChecked == true;
            _model.RefreshOnServerNotification = ServerNotificationCheckBox.IsChecked == true;
            _model.ProxyServer = server;
            _model.ProxyBypassList = BypassTextBox.Text.Trim();
            _model.ProxyUsername = ProxyUsernameTextBox.Text.Trim();
            _model.EncryptedProxyPassword = string.IsNullOrEmpty(ProxyPasswordBox.Password)
                ? null
                : CredentialProtector.Protect(ProxyPasswordBox.Password);
            _model.AutoRefreshIntervalMinutes = string.IsNullOrEmpty(refreshText) || refreshText == "0"
                ? null
                : int.Parse(refreshText);
            _model.DisplayMode = FloatingBallModeRadio.IsChecked == true
                ? UsageDisplayMode.FloatingBall
                : SystemTrayModeRadio.IsChecked == true
                    ? UsageDisplayMode.SystemTray
                    : UsageDisplayMode.Both;
            _model.Language = EnglishLanguageRadio.IsChecked == true
                ? AppLanguage.English
                : SimplifiedLanguageRadio.IsChecked == true ? AppLanguage.SimplifiedChinese : AppLanguage.TraditionalChinese;
            _model.UsageReadMode = DomOnlyRadio.IsChecked == true ? UsageReadMode.DomOnly
                : AppServerOnlyRadio.IsChecked == true ? UsageReadMode.AppServerOnly : UsageReadMode.AppServerWithDomFallback;
            _model.CodexExecutablePath = CodexExecutableTextBox.Text.Trim();

            await _settings.SaveAsync(_model, CancellationToken.None);
            if (_host is not null) await _host.ApplySettingsAsync(_model);
            DialogResult = true;
        }
        catch (InvalidDataException ex) { System.Windows.MessageBox.Show(ex.Message, L("設定錯誤", "Settings error"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
}

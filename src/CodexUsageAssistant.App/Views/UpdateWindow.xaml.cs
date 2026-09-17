using System.Windows;
using CodexEzMate.UpdateCore;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.Views;

public partial class UpdateWindow : Window
{
    private readonly AppUpdateService _updates;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateManifest? _manifest;
    public UpdateWindow(AppUpdateService updates, UpdateManifest? available = null)
    {
        _updates = updates; _manifest = available; InitializeComponent();
        Title += " — " + L("軟件更新", "Software update");
        CheckButton.Content = L("檢查更新", "Check updates"); InstallButton.Content = L("下載並安裝", "Download and install"); CloseButton.Content = L("關閉", "Close");
        Closed += (_, _) => _lifetime.Cancel();
        Loaded += async (_, _) => { if (_manifest is null) await CheckAsync(); else ShowManifest(); };
    }
    private async Task CheckAsync()
    {
        if (!AppUpdateService.IsConfigured)
        {
            StatusText.Text = L("此版本尚未設定更新公鑰。請先完成簽章及發佈設定。", "This build has no update public key. Complete signing and release setup first.");
            CheckButton.IsEnabled = false; return;
        }
        CheckButton.IsEnabled = false; InstallButton.IsEnabled = false;
        StatusText.Text = L("正在檢查更新…", "Checking for updates…");
        try { _manifest = await _updates.CheckAsync(_lifetime.Token); ShowManifest(); }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) StatusText.Text = L("檢查更新逾時。", "Update check timed out."); }
        catch (Exception) { StatusText.Text = L("未能檢查更新，請檢查網絡或稍後重試。", "Could not check updates. Check your connection or try again later."); }
        finally { CheckButton.IsEnabled = true; }
    }
    private void ShowManifest()
    {
        StatusText.Text = _manifest is null ? L($"目前版本 {AppUpdateService.CurrentVersion}，沒有可用更新。", $"Version {AppUpdateService.CurrentVersion}: no update available.")
            : L($"發現新版本 {_manifest.Version}（目前 {AppUpdateService.CurrentVersion}）", $"Version {_manifest.Version} is available (current {AppUpdateService.CurrentVersion}).");
        NotesText.Text = _manifest?.Notes ?? "";
        InstallButton.IsEnabled = _manifest is not null && AppUpdateService.CanInstall;
        if (_manifest is not null && !AppUpdateService.CanInstall) StatusText.Text += L("\n開發版不能自動替換程式。", "\nAutomatic installation is unavailable in development builds.");
    }
    private async void OnCheck(object sender, RoutedEventArgs e) => await CheckAsync();
    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_manifest is null) return;
        if (System.Windows.MessageBox.Show(this, L("請先關閉會話瀏覽器。更新會退出並重啟 Codex EzMate，是否繼續？", "Close Session Browser first. The update will exit and restart Codex EzMate. Continue?"), Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        InstallButton.IsEnabled = false; CheckButton.IsEnabled = false;
        try
        {
            var job = await _updates.DownloadAsync(_manifest, new Progress<string>(text => StatusText.Text = text), _lifetime.Token);
            StatusText.Text = L("簽章驗證完成，正在啟動安裝程式…", "Signature verified. Starting installer…");
            await _updates.StartInstallerAsync(job, _manifest);
        }
        catch (OperationCanceledException) { StatusText.Text = L("更新已取消或逾時。", "Update cancelled or timed out."); }
        catch (Exception) { StatusText.Text = L("更新失敗或簽章不正確；現有安裝未被替換。", "Update failed or signature invalid; the current installation was not replaced."); }
        finally { InstallButton.IsEnabled = AppUpdateService.CanInstall; CheckButton.IsEnabled = true; }
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
}

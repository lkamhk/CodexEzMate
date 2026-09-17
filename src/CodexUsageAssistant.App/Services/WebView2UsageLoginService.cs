using CodexUsageAssistant.Models;
using CodexUsageAssistant.Views;

namespace CodexUsageAssistant.Services;

public sealed class WebView2UsageLoginService : IUsageLoginService
{
    private readonly ISettingsService _settings;

    public WebView2UsageLoginService(ISettingsService settings) => _settings = settings;

    public async Task<UsageData?> ShowLoginAndReadUsageAsync(CancellationToken cancellationToken)
    {
        var owner = System.Windows.Application.Current.Windows.OfType<FloatingBallWindow>().FirstOrDefault();
        var settings = await _settings.LoadAsync(cancellationToken);
        var window = CreateLoginWindow(settings);
        window.Owner = owner;
        return await window.ShowAndReadAsync(cancellationToken, autoRead: false);
    }

    public async Task<UsageData?> RefreshUsageAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var settings = await _settings.LoadAsync(cancellationToken);
        var window = CreateLoginWindow(settings);
        var usage = await window.ShowAndReadAsync(timeout.Token, autoRead: true);
        cancellationToken.ThrowIfCancellationRequested();
        return timeout.IsCancellationRequested ? new UsageData
        {
            Status = UsageStatus.NetworkError,
            ErrorMessage = LocalizationService.Pick("DOM 背景讀取逾時。", "DOM background read timed out.")
        } : usage;
    }

    private static LoginWindow CreateLoginWindow(WindowPosition? settings)
    {
        var password = CredentialProtector.Unprotect(settings?.EncryptedProxyPassword);
        return new LoginWindow(
            settings?.ProxyEnabled == true ? settings.ProxyServer : null,
            settings?.ProxyBypassList,
            settings?.ProxyEnabled == true ? settings.ProxyUsername : null,
            password);
    }
}

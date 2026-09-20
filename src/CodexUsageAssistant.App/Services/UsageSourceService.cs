using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public interface IAppServerUsageReader
{
    Task<UsageData> ReadAsync(string? executablePath, CancellationToken cancellationToken);
}

public sealed class UsageSourceService(ISettingsService settings, IAppServerUsageReader appServer,
    IUsageLoginService dom, IAppServerSignInService? signIn = null) : IUsageLoginService
{
    public Task<UsageData?> RefreshUsageAsync(CancellationToken cancellationToken) => ReadAsync(false, cancellationToken);
    public Task<UsageData?> ShowLoginAndReadUsageAsync(CancellationToken cancellationToken) => ReadAsync(true, cancellationToken);

    private async Task<UsageData?> ReadAsync(bool interactive, CancellationToken cancellationToken)
    {
        var options = await settings.LoadAsync(cancellationToken) ?? new WindowPosition();
        var fallback = false;
        if (options.UsageReadMode != UsageReadMode.DomOnly)
        {
            if (interactive)
            {
                if (!await (signIn ?? new AppServerSignInService()).SignInAsync(options.CodexExecutablePath, cancellationToken)) return null;
                // An explicit Codex sign-in must refresh that account, never silently switch to DOM.
                return appServer is AppServerUsageService native
                    ? await native.ReadAfterSignInAsync(options.CodexExecutablePath, cancellationToken)
                    : await appServer.ReadAsync(options.CodexExecutablePath, cancellationToken);
            }
            var result = await appServer.ReadAsync(options.CodexExecutablePath, cancellationToken);
            if (result.Status == UsageStatus.Available || options.UsageReadMode == UsageReadMode.AppServerOnly) return result;
            cancellationToken.ThrowIfCancellationRequested();
            fallback = true;
        }
        var usage = interactive ? await dom.ShowLoginAndReadUsageAsync(cancellationToken) : await dom.RefreshUsageAsync(cancellationToken);
        if (usage is not null)
        {
            usage.DataSource = fallback ? "DOM (App Server fallback)" : "DOM";
            // Local estimates are explicitly labelled and do not claim to belong to the DOM account.
            await LocalTokenUsageService.EnrichTodayAsync(usage, cancellationToken);
        }
        return usage;
    }
}

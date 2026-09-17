namespace CodexUsageAssistant.Services;

public sealed record ProxyTestResult(bool Success, string Message, TimeSpan Elapsed);

public interface IProxyTestService
{
    Task<ProxyTestResult> TestAsync(string? proxyServer, string? username, string? password,
        CancellationToken cancellationToken);
}

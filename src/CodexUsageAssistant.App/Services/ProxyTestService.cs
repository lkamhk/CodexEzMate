using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CodexUsageAssistant.Services;

public sealed class ProxyTestService : IProxyTestService
{
    public async Task<ProxyTestResult> TestAsync(string? proxyServer, string? username, string? password,
        CancellationToken cancellationToken)
    {
        var normalized = ProxyConfiguration.NormalizeAndValidate(proxyServer);
        using var handler = new HttpClientHandler();
        if (normalized is not null)
        {
            handler.Proxy = new WebProxy(normalized)
            {
                Credentials = string.IsNullOrWhiteSpace(username)
                    ? null
                    : new NetworkCredential(username, password ?? string.Empty)
            };
            handler.UseProxy = true;
        }
        else handler.UseProxy = false;

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://ipinfo.io/json");
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            timer.Stop();
            if (!response.IsSuccessStatusCode)
                return new ProxyTestResult(false, $"ipinfo {L("回應", "returned")} HTTP {(int)response.StatusCode}", timer.Elapsed);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var ip = root.TryGetProperty("ip", out var ipValue) ? ipValue.GetString() : null;
            var country = root.TryGetProperty("country", out var countryValue) ? countryValue.GetString() : null;
            var region = root.TryGetProperty("region", out var regionValue) ? regionValue.GetString() : null;
            var organization = root.TryGetProperty("org", out var organizationValue) ? organizationValue.GetString() : null;
            var details = string.Join(" · ", new[] { ip, country, region, organization }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return new ProxyTestResult(true, $"{L("連線成功", "Connection successful")} ({details})", timer.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            timer.Stop();
            return new ProxyTestResult(false, $"{L("連線失敗", "Connection failed")}：{ex.Message}", timer.Elapsed);
        }
    }

    private static string L(string traditionalChinese, string english) =>
        LocalizationService.Pick(traditionalChinese, english);
}

using System.IO;

namespace CodexUsageAssistant.Services;

public static class ProxyConfiguration
{
    public static Uri? NormalizeAndValidate(string? proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer)) return null;
        var normalized = proxyServer.Contains("://", StringComparison.Ordinal) ? proxyServer.Trim() : $"http://{proxyServer.Trim()}";
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks4" or "socks5") ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port < 1)
            throw new InvalidDataException("Proxy 必須是有效的 HTTP、HTTPS、SOCKS4 或 SOCKS5 位址，且不可包含帳號密碼。");
        return uri;
    }

    public static string BuildBrowserArguments(string? proxyServer, string? bypassList)
    {
        var uri = NormalizeAndValidate(proxyServer);
        if (uri is null) return string.Empty;
        var arguments = $"--proxy-server={uri.AbsoluteUri.TrimEnd('/')}";
        if (string.IsNullOrWhiteSpace(bypassList)) return arguments;
        if (bypassList.Any(c => !(char.IsLetterOrDigit(c) || ".-_:*;,<>".Contains(c))))
            throw new InvalidDataException("Proxy bypass list 包含不允許的字元。");
        return $"{arguments} --proxy-bypass-list={bypassList}";
    }
}

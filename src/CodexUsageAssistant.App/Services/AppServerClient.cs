using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexUsageAssistant.Services;

internal sealed class AppServerClient : IAsyncDisposable
{
    private readonly Process _process;
    private int _nextId;
    private readonly Queue<JsonElement> _loginNotifications = new();
    private AppServerClient(Process process) => _process = process;

    public static async Task<AppServerClient> ConnectAsync(string? path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(AppServerUsageService.FindExecutable(path))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add("stdio://");
        var settings = await new JsonSettingsService().LoadAsync(token);
        if (settings is not null) ApplyProxyEnvironment(start, settings);
        var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch { process.Dispose(); throw; }
        var client = new AppServerClient(process);
        try
        {
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            await client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "codex_usage_assistant", title = "Codex EzMate", version = "1.21.4" },
                capabilities = new { experimentalApi = true }
            }, token);
            await client.SendAsync(new { method = "initialized", @params = new { } }, token);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        var id = ++_nextId;
        await SendAsync(new { id, method, @params = parameters }, token);
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(token);
            if (line is null) throw new IOException("App Server closed its output.");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out _) && root.TryGetProperty("method", out var notification) &&
                notification.GetString() == "account/login/completed" && _loginNotifications.Count < 8)
                _loginNotifications.Enqueue(root.Clone());
            if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var requestId))
            {
                await SendAsync(new { id = requestId.Clone(), error = new { code = -32601, message = "Unsupported client method" } }, token);
                continue;
            }
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number ||
                !responseId.TryGetInt32(out var value) || value != id) continue;
            if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("App Server rejected the read request.");
            return root.GetProperty("result").Clone();
        }
    }

    private async Task SendAsync(object message, CancellationToken token)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
        await _process.StandardInput.FlushAsync(token);
    }

    internal async Task<bool> WaitForLoginAsync(string loginId, CancellationToken token)
    {
        while (true)
        {
            JsonElement root;
            if (_loginNotifications.TryDequeue(out var pending)) root = pending;
            else
            {
                var line = await _process.StandardOutput.ReadLineAsync(token);
                if (line is null) throw new IOException("App Server closed its output.");
                using var json = JsonDocument.Parse(line);
                root = json.RootElement.Clone();
            }
            if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var requestId))
            {
                await SendAsync(new { id = requestId, error = new { code = -32601, message = "Unsupported client method" } }, token);
                continue;
            }
            if (!root.TryGetProperty("method", out var method) || method.GetString() != "account/login/completed" ||
                !root.TryGetProperty("params", out var parameters)) continue;
            if (parameters.TryGetProperty("loginId", out var id) && id.GetString() == loginId)
                return parameters.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        }
    }

    internal static void ApplyProxyEnvironment(ProcessStartInfo start, Models.WindowPosition settings)
    {
        if (!settings.ProxyEnabled) return;
        var proxy = ProxyConfiguration.NormalizeAndValidate(settings.ProxyServer);
        if (proxy is null) return;
        var builder = new UriBuilder(proxy);
        if (!string.IsNullOrWhiteSpace(settings.ProxyUsername))
        {
            builder.UserName = settings.ProxyUsername;
            builder.Password = CredentialProtector.Unprotect(settings.EncryptedProxyPassword) ?? "";
        }
        // Environment variables belong only to the child; never log credential-bearing URLs.
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            start.Environment[name] = builder.Uri.AbsoluteUri.TrimEnd('/');
        var bypass = (settings.ProxyBypassList ?? "").Replace(';', ',');
        start.Environment["NO_PROXY"] = bypass;
        start.Environment["no_proxy"] = bypass;
    }

    internal async Task ListenForUsageChangesAsync(Action changed, CancellationToken token)
    {
        // This client is dedicated to listening; never run RequestAsync concurrently.
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(token);
            if (line is null) throw new IOException("App Server closed its notification stream.");
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("id", out var id) && root.TryGetProperty("method", out _))
            {
                await SendAsync(new { id = id.Clone(), error = new { code = -32601, message = "Unsupported client method" } }, token);
                continue;
            }
            if (UsageChangeMonitor.IsUsageNotification(root)) changed();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception) { }
        finally { _process.Dispose(); }
    }
}

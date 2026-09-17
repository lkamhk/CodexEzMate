using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public enum AppServerHostState { Stopped, Starting, Ready, LoginRequired, ConnectionError }
internal enum HostProbe { Missing, Ready, LoginRequired, Incompatible, ConnectionError }

internal interface IHostProcess : IDisposable
{
    bool HasExited { get; }
    int Id { get; }
    void Stop();
}

internal interface IAppServerHostRuntime
{
    Task<HostProbe> ProbeAsync(int port, CancellationToken token);
    IHostProcess Start(WindowPosition settings);
}

internal sealed class AppServerHostRuntime : IAppServerHostRuntime
{
    internal static bool FillDefaults(WindowPosition settings, Func<string>? findExecutable = null)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(settings.HostWorkingDirectory))
        {
            settings.HostWorkingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageAssistant", "app-server-work");
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(settings.HostExecutablePath))
        {
            try
            {
                settings.HostExecutablePath = (findExecutable ?? (() => AppServerUsageService.FindExecutable(null)))();
                changed = true;
            }
            catch (Exception ex) when (CodexAppServerHost.IsExpected(ex)) { }
        }
        return changed;
    }

    internal static HostProbe ClassifyAccount(JsonElement result)
    {
        if (!result.TryGetProperty("account", out var account)) return HostProbe.Incompatible;
        if (account.ValueKind == JsonValueKind.Null) return HostProbe.LoginRequired;
        if (!account.TryGetProperty("type", out var type)) return HostProbe.Incompatible;
        return type.GetString() is "chatgpt" or "chatgptAuthTokens" ? HostProbe.Ready : HostProbe.LoginRequired;
    }

    public async Task<HostProbe> ProbeAsync(int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var tcp = new TcpClient();
            try { await tcp.ConnectAsync("127.0.0.1", port, timeout.Token); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return HostProbe.Missing; }
            await using var client = await HostWebSocketClient.ConnectAsync(port, timeout.Token);
            return ClassifyAccount(await client.RequestAsync("account/read", new { refreshToken = false }, timeout.Token));
        }
        catch (HostRpcException ex) { return ex.Code is 401 or 403 ? HostProbe.LoginRequired : HostProbe.ConnectionError; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return HostProbe.ConnectionError; }
        catch (Exception ex) when (ex is IOException or WebSocketException or SocketException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return HostProbe.Incompatible; }
    }

    public IHostProcess Start(WindowPosition settings)
    {
        if (System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
            throw new InvalidDataException("Run the host as the signed-in Windows user, not SYSTEM.");
        Validate(settings, true);
        var start = new ProcessStartInfo(settings.HostExecutablePath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = settings.HostWorkingDirectory!,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add($"ws://127.0.0.1:{settings.HostPort}");
        AppServerClient.ApplyProxyEnvironment(start, settings);
        var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.Start();
            started = true;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            return new OwnedProcess(process);
        }
        catch { if (started && !process.HasExited) process.Kill(true); process.Dispose(); throw; }
    }

    internal static void Validate(WindowPosition settings, bool createDirectory)
    {
        if (settings.HostPort is < 1 or > 65535) throw new InvalidDataException("Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(settings.HostExecutablePath) || !Path.IsPathFullyQualified(settings.HostExecutablePath) ||
            !settings.HostExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(settings.HostExecutablePath))
            throw new InvalidDataException("Select an existing absolute codex.exe path.");
        if (string.IsNullOrWhiteSpace(settings.HostWorkingDirectory) || !Path.IsPathFullyQualified(settings.HostWorkingDirectory))
            throw new InvalidDataException("Select an absolute empty working directory.");
        var directory = new DirectoryInfo(settings.HostWorkingDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Working directory ancestors must not be links.");
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) || Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                (!string.Equals(current.FullName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) &&
                 File.Exists(Path.Combine(current.FullName, ".codex", "config.toml"))))
                throw new InvalidDataException("Working directory inherits project instructions; select another location.");
        }
        if (directory.Exists && directory.EnumerateFileSystemInfos().Any()) throw new InvalidDataException("Working directory must be empty.");
        if (createDirectory) directory.Create();
    }

    private sealed class OwnedProcess(Process process) : IHostProcess
    {
        public bool HasExited => process.HasExited;
        public int Id => process.Id;
        public void Stop()
        {
            // WebSocket mode has no documented shutdown RPC. Only terminate this owned handle.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        public void Dispose() => process.Dispose();
    }
}

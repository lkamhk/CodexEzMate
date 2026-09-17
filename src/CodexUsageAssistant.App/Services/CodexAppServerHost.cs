using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed class CodexAppServerHost : IAsyncDisposable, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IAppServerHostRuntime _runtime;
    private readonly string _lockRoot;
    private readonly string _logPath;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _login;
    private Task? _loop;
    private IHostProcess? _owned;
    private FileStream? _startupLock;
    private WindowPosition _options = new();
    private string? _configuration;
    private DateTimeOffset _next;
    private DateTimeOffset? _startupDeadline;
    private DateTimeOffset? _healthySince;
    private int _failures;
    private int _checkNow;
    private string _detail = "";
    public AppServerHostState State { get; private set; } = AppServerHostState.Stopped;
    public bool OwnsServer { get; private set; }
    public bool IsRunning => _loop is { IsCompleted: false };
    public bool IsLoggingIn => _login is not null;
    public string Endpoint => $"ws://127.0.0.1:{_options.HostPort}";
    public string StatusText => State switch
    {
        AppServerHostState.Stopped => L("未啟動", "Not started"),
        AppServerHostState.Starting => L("啟動中", "Starting"),
        AppServerHostState.Ready => L("就緒", "Ready"),
        AppServerHostState.LoginRequired => L("需要登入", "Sign-in required"),
        _ => L("連線異常", "Connection error")
    };
    public string Details => $"{Endpoint} · {(OwnsServer ? L("本程式啟動", "Owned server") : L("未持有程序／外部 server", "No owned process / external server"))}\n{_detail}";
    public event Action? Changed;
    public Func<CancellationToken, Task<bool>>? BeforeStopAsync { get; set; }

    public CodexAppServerHost(ISettingsService settings) : this(settings, new AppServerHostRuntime(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageAssistant", "settings"),
        Path.Combine(InstallationPaths.Root, "logs", "codex-app-server-host.log")) { }

    internal CodexAppServerHost(ISettingsService settings, IAppServerHostRuntime runtime, string lockRoot, string logPath)
    { _settings = settings; _runtime = runtime; _lockRoot = lockRoot; _logPath = logPath; }

    public async Task InitializeAsync()
    {
        try
        {
            var options = await _settings.LoadAsync(CancellationToken.None) ?? new WindowPosition();
            var changed = AppServerHostRuntime.FillDefaults(options);
            if (changed) await _settings.SaveAsync(options, CancellationToken.None);
            await ApplySettingsAsync(options);
        }
        catch (Exception ex) when (IsExpected(ex)) { SetState(AppServerHostState.ConnectionError, L("請在 Server 代管設定中選擇有效 Codex 路徑及空白資料夾。", "Select a valid Codex executable and empty working directory in host settings.")); }
    }

    public async Task ApplySettingsAsync(WindowPosition options)
    {
        var config = Fingerprint(options);
        if (_configuration == config) return;
        await StopAsync();
        _options = JsonSerializer.Deserialize<WindowPosition>(JsonSerializer.Serialize(options))!;
        _configuration = config;
        if (options.HostAutoStart) await StartAsync();
        else Changed?.Invoke();
    }

    private static string Fingerprint(WindowPosition x) => JsonSerializer.Serialize(new { x.HostAutoStart, x.HostExecutablePath, x.HostPort,
        x.HostWorkingDirectory, x.ProxyEnabled, x.ProxyServer, x.ProxyUsername, x.EncryptedProxyPassword, x.ProxyBypassList });

    public async Task StartAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (IsRunning) { CheckNow(); return; }
            if (_options.HostPort is < 1 or > 65535) throw new InvalidDataException("Invalid port.");
            _lifetime = new CancellationTokenSource();
            _next = DateTimeOffset.MinValue;
            _failures = 0;
            SetState(AppServerHostState.Starting, "");
            _loop = Task.Run(() => RunAsync(_lifetime.Token));
        }
        finally { _operations.Release(); }
    }

    public void CheckNow() => Interlocked.Exchange(ref _checkNow, 1);

    public async Task CheckEndpointAsync()
    {
        if (IsRunning) { CheckNow(); return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await _runtime.ProbeAsync(_options.HostPort, timeout.Token);
        SetState(result switch
        {
            HostProbe.Ready => AppServerHostState.Ready,
            HostProbe.LoginRequired => AppServerHostState.LoginRequired,
            HostProbe.Missing => AppServerHostState.Stopped,
            _ => AppServerHostState.ConnectionError
        }, L("此操作只檢查端點，不啟動或停止程序。", "Endpoint check only; no process is started or stopped."));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_owned is { HasExited: true })
                {
                    StopOwned();
                    ScheduleRetry();
                }
                if (DateTimeOffset.UtcNow >= _next || Interlocked.Exchange(ref _checkNow, 0) != 0)
                {
                    try { await CheckCycleAsync(token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception ex) when (IsExpected(ex))
                    {
                        if (_startupDeadline is not null) StopOwned();
                        ScheduleRetry();
                    }
                }
                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { StopOwned(); }
    }

    private async Task CheckCycleAsync(CancellationToken token)
    {
        if (_startupLock is null)
        {
            Directory.CreateDirectory(_lockRoot);
            try { _startupLock = new FileStream(Path.Combine(_lockRoot, $"app-server-{_options.HostPort}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                _next = DateTimeOffset.UtcNow.AddSeconds(2);
                SetState(AppServerHostState.Starting, L("另一實例正在檢查／啟動此端點。", "Another instance is checking or starting this endpoint."));
                return;
            }
        }
        var result = await _runtime.ProbeAsync(_options.HostPort, token);
        if (result is HostProbe.Ready or HostProbe.LoginRequired)
        {
            _startupDeadline = null;
            ReleaseStartupLock();
            _healthySince ??= DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - _healthySince >= TimeSpan.FromSeconds(60)) _failures = 0;
            _next = DateTimeOffset.UtcNow.AddSeconds(30);
            SetState(result == HostProbe.Ready ? AppServerHostState.Ready : AppServerHostState.LoginRequired,
                result == HostProbe.Ready ? L("相容客戶端可連接此端點。", "Compatible clients can connect to this endpoint.") : L("請按登入 Codex；不會因登入狀態重啟 server。", "Sign in to Codex; authentication does not trigger server restarts."));
            return;
        }
        _healthySince = null;
        if (_startupDeadline is { } deadline)
        {
            if (DateTimeOffset.UtcNow < deadline) { _next = DateTimeOffset.UtcNow.AddMilliseconds(500); return; }
            StopOwned(); ScheduleRetry(); return;
        }
        if (result != HostProbe.Missing || _owned is not null)
        {
            ReleaseStartupLock();
            _next = DateTimeOffset.UtcNow.AddSeconds(30);
            SetState(AppServerHostState.ConnectionError, L("端口已有服務但握手／帳戶檢查失敗。請檢查端口、版本及網絡；不會終止該程序。", "A listener exists but handshake/account check failed. Check the port, version and network; no process will be terminated."));
            return;
        }
        SetState(AppServerHostState.Starting, L("正在背景啟動 Codex App Server。", "Starting Codex App Server in the background."));
        try
        {
            _owned = _runtime.Start(_options);
            OwnsServer = true;
            _startupDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            _next = DateTimeOffset.UtcNow.AddMilliseconds(500);
            Log("owned_start", _owned.Id);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ReleaseStartupLock();
            ScheduleRetry();
            SetState(AppServerHostState.ConnectionError, L("啟動失敗；請檢查 Codex 完整路徑、空白工作資料夾及 Proxy。", "Start failed; check the absolute Codex path, empty working directory and proxy.") + $" {_next.ToLocalTime():HH:mm:ss}");
        }
    }

    internal static int RetrySeconds(int failure) => new[] { 2, 5, 15, 30, 60 }[Math.Clamp(failure, 0, 4)];
    private void ScheduleRetry()
    {
        ReleaseStartupLock();
        var seconds = RetrySeconds(_failures);
        _failures = Math.Min(_failures + 1, 4);
        _next = DateTimeOffset.UtcNow.AddSeconds(seconds);
        SetState(AppServerHostState.Starting, L($"{seconds} 秒後重試（{_next.ToLocalTime():HH:mm:ss}）。", $"Retry in {seconds}s ({_next.ToLocalTime():HH:mm:ss})."));
        Log("retry", seconds);
    }

    public async Task RestartAsync()
    {
        if (!OwnsServer && IsRunning) { CheckNow(); SetState(State, L("外部 server 由原啟動者管理；只重新檢查連線。", "External server is managed by its owner; checking the connection only.")); return; }
        await StopAsync(); await StartAsync();
    }

    public async Task StopAsync()
    {
        if (BeforeStopAsync is not null)
        {
            using var pauseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (!await BeforeStopAsync(pauseTimeout.Token).ConfigureAwait(false))
                throw new InvalidOperationException("A background Goal could not be paused; server remains running.");
        }
        await _operations.WaitAsync();
        try
        {
            _login?.Cancel();
            _lifetime?.Cancel();
            if (_loop is not null) await _loop;
            _loop = null;
            _lifetime?.Dispose(); _lifetime = null;
            StopOwned();
            SetState(AppServerHostState.Stopped, "");
        }
        finally { _operations.Release(); }
    }

    public void StopOwnedImmediately()
    {
        _lifetime?.Cancel(); _login?.Cancel();
        StopOwned();
    }

    private void StopOwned()
    {
        var process = Interlocked.Exchange(ref _owned, null);
        if (process is not null)
        {
            try { Log("owned_stop", process.Id); process.Stop(); }
            catch (Exception ex) when (IsExpected(ex)) { Log("owned_stop_failed", 0); }
            finally { process.Dispose(); }
        }
        OwnsServer = false; _startupDeadline = null; _healthySince = null;
        ReleaseStartupLock();
    }
    private void ReleaseStartupLock() { _startupLock?.Dispose(); _startupLock = null; }

    public async Task LoginAsync(Action<Uri> openBrowser)
    {
        if (_login is not null) return;
        using var login = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _login = login;
        HostWebSocketClient? client = null;
        string? loginId = null;
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(login.Token);
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            client = await HostWebSocketClient.ConnectAsync(_options.HostPort, connect.Token);
            var response = await client.RequestAsync("account/login/start", new { type = "chatgpt" }, connect.Token);
            loginId = response.GetProperty("loginId").GetString();
            var uri = new Uri(response.GetProperty("authUrl").GetString()!);
            if (uri.Scheme != "https" || !(uri.Host == "chatgpt.com" || uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Unexpected login URL.");
            openBrowser(uri);
            SetState(AppServerHostState.LoginRequired, L("請在瀏覽器完成登入。", "Complete sign-in in your browser."));
            var success = await client.WaitForLoginAsync(loginId!, login.Token);
            if (success) { loginId = null; CheckNow(); }
            else SetState(AppServerHostState.LoginRequired, L("登入未完成，請重試。", "Sign-in did not complete; try again."));
        }
        catch (Exception ex) when (IsExpected(ex)) { SetState(AppServerHostState.LoginRequired, L("登入已取消或未能完成，請重新檢查。", "Sign-in cancelled or failed; check again.")); }
        finally
        {
            if (client is not null)
            {
                if (loginId is not null)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await using var cancelClient = await HostWebSocketClient.ConnectAsync(_options.HostPort, timeout.Token);
                        await cancelClient.RequestAsync("account/login/cancel", new { loginId }, timeout.Token);
                    }
                    catch (Exception ex) when (IsExpected(ex)) { }
                }
                await client.DisposeAsync();
            }
            _login = null; Changed?.Invoke();
        }
    }
    public void CancelLogin() => _login?.Cancel();

    public async Task<IReadOnlyList<string>> ListModelsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await HostWebSocketClient.ConnectAsync(_options.HostPort, timeout.Token);
        var models = new List<string>();
        var seen = new HashSet<string>();
        string? cursor = null;
        do
        {
            var response = await client.RequestAsync("model/list", new { limit = 100, cursor, includeHidden = false }, timeout.Token);
            foreach (var item in response.GetProperty("data").EnumerateArray())
                models.Add(item.GetProperty("model").GetString() ?? "");
            cursor = response.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor) && seen.Add(cursor));
        return models.Distinct().ToList();
    }

    private void SetState(AppServerHostState state, string detail)
    {
        if (State != state) Log(state.ToString(), 0);
        State = state; _detail = detail; Changed?.Invoke();
    }
    private void Log(string eventName, int value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 1024 * 1024) File.Move(_logPath, _logPath + ".1", true);
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {eventName} {value}\n", System.Text.Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    internal static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
        ArgumentException or System.ComponentModel.Win32Exception or System.Net.WebSockets.WebSocketException or
        JsonException or KeyNotFoundException or OperationCanceledException or HostRpcException;
    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
    public async ValueTask DisposeAsync() => await StopAsync();
    public void Dispose() => StopOwnedImmediately();
}

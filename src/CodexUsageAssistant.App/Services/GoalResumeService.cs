using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed class GoalResumeService : IGoalResumeService, IAsyncDisposable, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly Func<CancellationToken, Task> _ensureHost;
    private readonly Func<int, CancellationToken, Task<IGoalRpcConnection>> _connect;
    private readonly ITrayNotificationService _notifications;
    private readonly string _statePath;
    private readonly TimeSpan _confirmationTimeout;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly SemaphoreSlim _persistence = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private IGoalRpcConnection? _connection;
    private Task? _events;
    private Task? _recovery;
    private FileStream? _lease;
    private FileStream? _globalLease;
    private OwnedGoal? _owned;
    private readonly Dictionary<string, Pending> _pending = [];
    private readonly Dictionary<string, JsonElement> _fileChanges = [];
    private TaskCompletionSource<bool>? _started;
    private TaskCompletionSource<bool>? _stopped;
    private bool _restored;
    private bool _recoveryBlocked;
    private volatile bool _exiting;
    internal void PrepareToExit(bool value) => _exiting = value;
    private string? _lastGoalStatus;
    private bool _turnIdleConfirmed;
    private int _port;
    private int _connectedPort;
    private string _connectionId = "";

    private sealed record OwnedGoal(ConversationTarget Target, string Fingerprint, int Port, string? TurnId, bool Submitted);
    private sealed record Pending(GoalPendingRequest View, JsonElement Id, string ConnectionId);
    private sealed class GoalLeaseBusyException : IOException { }
    public bool HasActiveWork { get { lock (_sync) return _owned is not null; } }
    public GoalExecutionState State { get; private set; } = new(null, null, ConversationResumeStatus.Unknown, "");
    public IReadOnlyList<GoalPendingRequest> PendingRequests { get { lock (_sync) return _pending.Values.Select(p => p.View).ToArray(); } }
    public event Action? Changed;
    public event Action? UsageChanged;

    public GoalResumeService(ISettingsService settings, CodexAppServerHost host, ITrayNotificationService notifications)
        : this(settings, async token =>
        {
            await host.StartAsync().ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
            while (host.State == AppServerHostState.Starting && DateTimeOffset.UtcNow < deadline) await Task.Delay(200, token).ConfigureAwait(false);
            if (host.State != AppServerHostState.Ready) throw new InvalidOperationException("App Server is not ready.");
        }, GoalRpcConnection.ConnectAsync, notifications,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageAssistant", "settings", "goal-runtime.json")) { }

    internal GoalResumeService(ISettingsService settings, Func<CancellationToken, Task> ensureHost,
        Func<int, CancellationToken, Task<IGoalRpcConnection>> connect, ITrayNotificationService notifications, string statePath, TimeSpan? confirmationTimeout = null)
    { _settings = settings; _ensureHost = ensureHost; _connect = connect; _notifications = notifications; _statePath = statePath; _confirmationTimeout = confirmationTimeout ?? TimeSpan.FromSeconds(15); }

    public Task<ConversationResumeResult> ResumeAsync(ConversationTarget target, CancellationToken token) => ResumeCoreAsync(target, false, token);
    public Task<ConversationResumeResult> ContinuePausedAsync(ConversationTarget target, CancellationToken token) => ResumeCoreAsync(target, true, token);

    public async Task<UsageData> ReadUsageAsync(CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!HasActiveWork)
            {
                _port = (await _settings.LoadAsync(token).ConfigureAwait(false))?.HostPort ?? 4500;
                await _ensureHost(token).ConfigureAwait(false);
            }
            await ConnectAsync(token).ConfigureAwait(false);
            if (HasActiveWork && State.Status == ConversationResumeStatus.PendingConfirmation) await ReconcileAsync(token).ConfigureAwait(false);
            var usage = AppServerUsageService.ParseRateLimits(await _connection!.RequestAsync("account/rateLimits/read", null, token).ConfigureAwait(false), DateTimeOffset.Now);
            if (usage.Status == UsageStatus.Available)
            {
                try
                {
                    var activity = await _connection.RequestAsync("account/usage/read", null, token).ConfigureAwait(false);
                    AppServerUsageService.ApplyTokenUsage(usage, activity, DateTimeOffset.Now);
                }
                catch (HostRpcException) { }
                await LocalTokenUsageService.EnrichTodayAsync(usage, token).ConfigureAwait(false);
            }
            return usage;
        }
        finally { _operation.Release(); }
    }

    internal async Task InitializeAsync(CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_restored) return;
            _restored = true;
            if (!File.Exists(_statePath)) return;
            var saved = JsonSerializer.Deserialize<OwnedGoal>(await File.ReadAllTextAsync(_statePath, token).ConfigureAwait(false));
            if (saved?.Target?.ThreadId is not { } id || !Guid.TryParse(id, out _) || string.IsNullOrWhiteSpace(saved.Fingerprint) || saved.Port is < 1 or > 65535)
                throw new InvalidDataException("Invalid background Goal recovery record.");
            AcquireLease(id);
            lock (_sync) _owned = saved;
            _port = saved.Port;
            Publish(ConversationResumeStatus.PendingConfirmation, L("正在核對上次背景工作，未重複提交。", "Reconciling the previous background run; no request is resubmitted."));
            await ConnectAsync(token).ConfigureAwait(false);
            await ReconcileAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        {
            if (ex is JsonException or InvalidDataException) _recoveryBlocked = true;
            Publish(ConversationResumeStatus.PendingConfirmation, L("上次背景狀態未確認，請重新檢查。", "Previous background state is unconfirmed; check again."));
            if (HasActiveWork) _recovery = Task.Run(RecoverAsync);
        }
        finally { _operation.Release(); }
    }

    private async Task<ConversationResumeResult> ResumeCoreAsync(ConversationTarget target, bool manual, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_recoveryBlocked && File.Exists(_statePath))
                return Result(target, ConversationResumeStatus.PendingConfirmation, L("背景恢復記錄無法讀取，未啟動任何 Goal。請先核對原工作狀態。", "Recovery record is unreadable; no Goal was started. Reconcile the original work first."));
            _recoveryBlocked = false;
            if (_exiting) return Result(target, ConversationResumeStatus.NoActionNeeded, L("程式正在退出，沒有接手新工作。", "The app is exiting; no new work was started."));
            if (HasActiveWork)
            {
                if (_connection?.IsConnected != true) { await ConnectAsync(token).ConfigureAwait(false); await ReconcileAsync(token).ConfigureAwait(false); }
                if (HasActiveWork) return Result(target, ConversationResumeStatus.Busy, L("已有背景 Goal 或待確認工作。", "A background Goal or unconfirmed run already exists."));
            }
            if (!Guid.TryParse(target.ThreadId, out _)) return Result(target, ConversationResumeStatus.NotFound, L("請重新掃描並選擇有 Thread ID 的對話。", "Rescan and select a conversation with a Thread ID."));
            var settings = await _settings.LoadAsync(token).ConfigureAwait(false) ?? new WindowPosition();
            _port = settings.HostPort;
            if (target.Cwd?.StartsWith("\\\\wsl", StringComparison.OrdinalIgnoreCase) == true)
                return Result(target, ConversationResumeStatus.Unsupported, L("首版背景接手未支援 WSL。", "Background takeover does not yet support WSL."));
            await _ensureHost(token).ConfigureAwait(false);
            await ConnectAsync(token).ConfigureAwait(false);
            var rpc = _connection!;
            var usage = AppServerUsageService.ParseRateLimits(await rpc.RequestAsync("account/rateLimits/read", null, token).ConfigureAwait(false), DateTimeOffset.Now);
            if (!GoalProtocol.UsageAllowsResume(usage)) return Result(target, ConversationResumeStatus.WaitingForReset, L("App Server 尚未明確允許使用。", "App Server has not confirmed usage is available."));
            var goal = GoalProtocol.Property(await rpc.RequestAsync("thread/goal/get", new { threadId = target.ThreadId }, token).ConfigureAwait(false), "goal");
            target.GoalStatus = GoalProtocol.Text(goal, "status");
            if (!GoalProtocol.CanResumeGoal(target.GoalStatus, manual)) return Result(target, ConversationResumeStatus.NoActionNeeded, L("只接手 usageLimited Goal；人工繼續另允許 paused。", "Only usageLimited Goals are automatic; explicit continuation also permits paused."));
            var thread = GoalProtocol.Property(await rpc.RequestAsync("thread/read", new { threadId = target.ThreadId, includeTurns = false }, token).ConfigureAwait(false), "thread");
            target.Cwd = GoalProtocol.Text(thread, "cwd");
            if (GoalProtocol.InternalSource(GoalProtocol.Property(thread, "source")) || GoalProtocol.Property(thread, "ephemeral").ValueKind == JsonValueKind.True ||
                string.IsNullOrWhiteSpace(target.Cwd) || target.Cwd.StartsWith("\\\\wsl", StringComparison.OrdinalIgnoreCase) ||
                !Path.IsPathFullyQualified(target.Cwd) || !Directory.Exists(target.Cwd))
                return Result(target, ConversationResumeStatus.Unsupported, L("此對話不是可接手的本機已保存對話。", "This is not a supported persisted local conversation."));
            if (GoalProtocol.Status(GoalProtocol.Property(thread, "status")) == "active") return Result(target, ConversationResumeStatus.Busy, L("對話正在執行，沒有接手。", "The conversation is active; no takeover was attempted."));
            AcquireLease(target.ThreadId!);
            var resumed = await rpc.RequestAsync("thread/resume", new { threadId = target.ThreadId, excludeTurns = true }, token).ConfigureAwait(false);
            var live = GoalProtocol.Property(resumed, "thread");
            var originalModel = GoalProtocol.Text(thread, "model");
            var resumedModel = GoalProtocol.Text(resumed, "model");
            if (originalModel is not null && resumedModel is not null && originalModel != resumedModel)
            { ReleaseLease(); return Result(target, ConversationResumeStatus.Unsupported, L("Server 未保留原模型，已停止接手。", "Server did not retain the original model; takeover stopped.")); }
            if (GoalProtocol.Status(GoalProtocol.Property(live, "status")) == "active" || GoalProtocol.Property(resumed, "readOnly").ValueKind == JsonValueKind.True ||
                GoalProtocol.Property(live, "readOnly").ValueKind == JsonValueKind.True || GoalProtocol.Property(live, "isReadOnly").ValueKind == JsonValueKind.True)
            { ReleaseLease(); return Result(target, ConversationResumeStatus.Busy, L("對話已被其他執行者使用。", "The conversation is in use by another runner.")); }
            goal = GoalProtocol.Property(await rpc.RequestAsync("thread/goal/get", new { threadId = target.ThreadId }, token).ConfigureAwait(false), "goal");
            if (!GoalProtocol.CanResumeGoal(GoalProtocol.Text(goal, "status"), manual))
            { ReleaseLease(); return Result(target, ConversationResumeStatus.NoActionNeeded, L("Goal 狀態已變更，沒有操作。", "The Goal state changed; no action was taken.")); }
            target.Compatibility = L("已取得背景接手權限", "Background takeover acquired");
            lock (_sync)
            {
                if (_exiting) { ReleaseLease(); return Result(target, ConversationResumeStatus.NoActionNeeded, L("程式正在退出。", "The app is exiting.")); }
                _owned = new(target, GoalProtocol.GoalFingerprint(goal), _port, null, true);
                _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _lastGoalStatus = "active";
                _turnIdleConfirmed = false;
            }
            await PersistAsync().ConfigureAwait(false);
            Publish(ConversationResumeStatus.Resuming, L("正在背景恢復原 Goal…", "Resuming the original Goal in the background…"));
            var activation = await rpc.RequestAsync("thread/goal/set", new { threadId = target.ThreadId, status = "active" }, token).ConfigureAwait(false);
            var activatedStatus = GoalProtocol.Text(GoalProtocol.Property(activation, "goal"), "status");
            if (activatedStatus is not null && activatedStatus != "active")
            { await FinishAsync(activatedStatus).ConfigureAwait(false); return Result(target, State.Status, State.Message); }
            var started = _started;
            if (started is not null)
            {
                try { await started.Task.WaitAsync(_confirmationTimeout, token).ConfigureAwait(false); }
                catch (TimeoutException) { Publish(ConversationResumeStatus.PendingConfirmation, L("Goal 已啟用，等待實際執行事件；不重複提交。", "Goal activated; waiting for an execution event without resubmitting.")); }
            }
            return Result(target, State.Status, State.Message);
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        {
            if (!HasActiveWork) ReleaseLease();
            var rpcError = ex as HostRpcException;
            var writerBusy = rpcError is { Failure: HostRpcFailure.ActiveWriter, Method: "thread/resume" };
            var status = HasActiveWork ? ConversationResumeStatus.PendingConfirmation : ex is GoalLeaseBusyException || writerBusy ? ConversationResumeStatus.Busy
                : rpcError?.Code == -32601 ? ConversationResumeStatus.Unsupported : ConversationResumeStatus.Failed;
            var message = L("背景接手未能確認；請檢查版本、工作目錄及對話是否由其他客戶端使用。", "Background takeover is unconfirmed; check version, working directory and other clients.");
            if (writerBusy)
            {
                target.Compatibility = L("原客戶端持有寫入權", "Writer held by another client");
                message = L("另一個 Codex 客戶端仍持有此會話的寫入權，暫時無法接手。請在原客戶端關閉此對話；若仍被佔用，正常退出原客戶端。保持監控會自動重試，也可按「立即檢查」。",
                    "Another Codex client still holds this conversation's writer. Close the conversation in its original client; if it remains in use, exit that client normally. Monitoring will retry, or select Check now.");
            }
            else if (rpcError is not null)
            {
                target.Compatibility = L("App Server 請求未完成", "App Server request failed");
                message = L($"App Server 未接受 {rpcError.Method ?? "RPC"}（錯誤碼 {rpcError.Code}）。請檢查 Codex 版本及原客戶端的對話狀態。",
                    $"App Server rejected {rpcError.Method ?? "RPC"} (code {rpcError.Code}). Check the Codex version and conversation state in the original client.");
            }
            if (HasActiveWork) Publish(status, message);
            return Result(target, status, message);
        }
        finally { _operation.Release(); }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        if (_connection?.IsConnected == true && _connectedPort == _port) return;
        _connectionId = "";
        var old = _connection; _connection = null;
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        if (_port == 0) _port = (await _settings.LoadAsync(token).ConfigureAwait(false))?.HostPort ?? 4500;
        var connection = await _connect(_port, token).ConfigureAwait(false);
        _connection = connection; _connectedPort = _port; _connectionId = Guid.NewGuid().ToString("N");
        var identity = _connectionId;
        lock (_sync) { _pending.Clear(); _fileChanges.Clear(); }
        _events = Task.Run(() => ProcessEventsAsync(connection, identity));
    }

    private async Task ProcessEventsAsync(IGoalRpcConnection connection, string identity)
    {
        try
        {
            await foreach (var message in connection.Events(_lifetime.Token).ConfigureAwait(false))
            {
                if (_connectionId != identity) break;
                var method = GoalProtocol.Text(message, "method") ?? "";
                var parameters = GoalProtocol.Property(message, "params");
                if (method == "account/rateLimits/updated") { UsageChanged?.Invoke(); continue; }
                if (message.TryGetProperty("id", out var id)) { await ReceiveRequestAsync(connection, identity, id, method, parameters).ConfigureAwait(false); continue; }
                var threadId = GoalProtocol.Text(parameters, "threadId");
                OwnedGoal? owned; lock (_sync) owned = _owned;
                if (owned is null || threadId != owned.Target.ThreadId) continue;
                if (method == "item/started")
                {
                    var item = GoalProtocol.Property(parameters, "item");
                    if (GoalProtocol.Text(item, "type") == "fileChange" && item.GetRawText().Length <= 262144 && GoalProtocol.Text(item, "id") is { } itemId)
                        lock (_sync) { if (_fileChanges.Count < 32) _fileChanges[itemId] = item.Clone(); }
                }
                else if (method == "item/completed")
                {
                    var itemId = GoalProtocol.Text(GoalProtocol.Property(parameters, "item"), "id");
                    if (itemId is not null) lock (_sync) _fileChanges.Remove(itemId);
                }
                else if (method == "turn/started")
                {
                    var turnId = GoalProtocol.Text(GoalProtocol.Property(parameters, "turn"), "id");
                    if (turnId is null) continue;
                    lock (_sync) { if (_owned is not null) _owned = _owned with { TurnId = turnId }; _turnIdleConfirmed = false; }
                    await PersistAsync().ConfigureAwait(false);
                    Publish(ConversationResumeStatus.Resumed, L("Goal 正在背景執行。", "Goal is running in the background."));
                    _started?.TrySetResult(true);
                }
                else if (method == "turn/completed")
                {
                    var completedId = GoalProtocol.Text(GoalProtocol.Property(parameters, "turn"), "id");
                    if (owned.TurnId is not null && completedId != owned.TurnId) continue;
                    lock (_sync) { if (_owned is not null) _owned = _owned with { TurnId = null }; _turnIdleConfirmed = true; _stopped?.TrySetResult(true); }
                    await PersistAsync().ConfigureAwait(false);
                    if (_lastGoalStatus is "complete" or "paused" or "blocked" or "budgetLimited" or "usageLimited") await FinishAsync(_lastGoalStatus).ConfigureAwait(false);
                }
                else if (method == "thread/goal/updated")
                {
                    _lastGoalStatus = GoalProtocol.Text(GoalProtocol.Property(parameters, "goal"), "status");
                    lock (_sync) owned = _owned;
                    if (owned?.TurnId is null && _turnIdleConfirmed && _lastGoalStatus is "complete" or "paused" or "blocked" or "budgetLimited" or "usageLimited")
                        await FinishAsync(_lastGoalStatus).ConfigureAwait(false);
                }
                else if (method == "serverRequest/resolved")
                {
                    var requestId = GoalProtocol.Property(parameters, "requestId").GetRawText();
                    lock (_sync) foreach (var key in _pending.Where(x => x.Value.Id.GetRawText() == requestId).Select(x => x.Key).ToArray()) _pending.Remove(key);
                    Changed?.Invoke();
                }
            }
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex)) { }
        finally
        {
            if (!_lifetime.IsCancellationRequested && _connectionId == identity && HasActiveWork)
            {
                lock (_sync) _pending.Clear();
                Publish(ConversationResumeStatus.PendingConfirmation, L("連線中斷，正在核對原工作；不重新提交。", "Disconnected; reconciling the original run without resubmitting."));
                _recovery = Task.Run(RecoverAsync);
            }
        }
    }

    private async Task RecoverAsync()
    {
        while (!_lifetime.IsCancellationRequested && HasActiveWork)
        {
            try
            {
                await Task.Delay(5000, _lifetime.Token).ConfigureAwait(false);
                await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try { await ConnectAsync(_lifetime.Token).ConfigureAwait(false); await ReconcileAsync(_lifetime.Token).ConfigureAwait(false); return; }
                finally { _operation.Release(); }
            }
            catch (Exception ex) when (CodexAppServerHost.IsExpected(ex)) { }
        }
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        OwnedGoal? owned; lock (_sync) owned = _owned;
        if (owned is null) return;
        var goal = GoalProtocol.Property(await _connection!.RequestAsync("thread/goal/get", new { threadId = owned.Target.ThreadId }, token).ConfigureAwait(false), "goal");
        if (goal.ValueKind != JsonValueKind.Object || GoalProtocol.GoalFingerprint(goal) != owned.Fingerprint)
        { await FinishAsync("changed").ConfigureAwait(false); return; }
        var state = GoalProtocol.Text(goal, "status");
        _lastGoalStatus = state;
        var resumed = await _connection.RequestAsync("thread/resume", new { threadId = owned.Target.ThreadId, excludeTurns = true }, token).ConfigureAwait(false);
        var threadState = GoalProtocol.Status(GoalProtocol.Property(GoalProtocol.Property(resumed, "thread"), "status"));
        var active = threadState == "active";
        if (!active && threadState == "idle")
        {
            lock (_sync) { if (_owned is not null) _owned = _owned with { TurnId = null }; _turnIdleConfirmed = true; }
            if (state != "active") { await FinishAsync(state ?? "unknown").ConfigureAwait(false); return; }
        }
        if (active)
        {
            var turn = await FindActiveTurnAsync(owned.Target.ThreadId!, token).ConfigureAwait(false);
            lock (_sync) { if (_owned is not null && turn is not null) _owned = _owned with { TurnId = turn }; }
            await PersistAsync().ConfigureAwait(false);
        }
        Publish(active && state == "active" ? ConversationResumeStatus.Resumed : ConversationResumeStatus.PendingConfirmation,
            active ? L("原背景 turn 仍在執行，可要求暫停。", "The original background turn is still active; pause is available.") : L("等待原 Goal 執行狀態確認。", "Awaiting confirmation of the original Goal state."));
    }

    private async Task ReceiveRequestAsync(IGoalRpcConnection connection, string identity, JsonElement id, string method, JsonElement parameters)
    {
        OwnedGoal? owned; lock (_sync) owned = _owned;
        if (owned is null || GoalProtocol.Text(parameters, "threadId") != owned.Target.ThreadId)
        { await connection.ReplyAsync(id, null, true, _lifetime.Token).ConfigureAwait(false); return; }
        lock (_sync)
            if (_pending.Values.Any(x => x.ConnectionId == identity && x.Id.GetRawText() == id.GetRawText())) return;
        var decisions = GoalProtocol.Decisions(method, parameters);
        var details = parameters.Clone();
        if (method == "item/fileChange/requestApproval")
        {
            var itemId = GoalProtocol.Text(parameters, "itemId");
            JsonElement item; lock (_sync) item = itemId is not null && _fileChanges.TryGetValue(itemId, out var found) ? found : default;
            if (item.ValueKind == JsonValueKind.Undefined) decisions = [];
            else details = JsonSerializer.SerializeToElement(new { request = parameters, changes = GoalProtocol.Property(item, "changes") });
        }
        if (decisions.Count == 0 || parameters.GetRawText().Length > 262144)
        {
            await connection.ReplyAsync(id, null, true, _lifetime.Token).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                await PauseAsync(_lifetime.Token).ConfigureAwait(false);
                Publish(ConversationResumeStatus.Unsupported, L("需要原客戶端工具／能力，已要求暫停。", "Original-client tools or capabilities are required; pause was requested."));
                _notifications.ShowNotification(L("Goal 需要原客戶端", "Goal needs its original client"), owned.Target.Title);
            });
            return;
        }
        var key = identity + ":" + Guid.NewGuid().ToString("N");
        var description = (GoalProtocol.Text(parameters, "command") ?? GoalProtocol.Text(parameters, "reason") ?? method) + "\n" + JsonSerializer.Serialize(details,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var view = new GoalPendingRequest(key, method, owned.Target.ThreadId!, GoalProtocol.Text(parameters, "turnId"), description, parameters.Clone(), decisions);
        lock (_sync)
        {
            _pending[key] = new(view, id.Clone(), identity);
            if (_owned is not null && _owned.TurnId is null) _owned = _owned with { TurnId = view.TurnId };
            _turnIdleConfirmed = false;
        }
        await PersistAsync().ConfigureAwait(false);
        Publish(ConversationResumeStatus.AwaitingApproval, L("有待處理請求，請在 Goal 視窗查看。", "A request is waiting; review it in the Goal window."));
        _started?.TrySetResult(true);
        _notifications.ShowNotification(L("Goal 等待你的回覆", "Goal is waiting for your response"), owned.Target.Title);
    }

    public async Task RespondAsync(string key, string decision, IReadOnlyDictionary<string, string[]>? answers, CancellationToken token)
    {
        Pending request;
        lock (_sync)
        {
            if (!_pending.TryGetValue(key, out request!) || request.ConnectionId != _connectionId || _owned?.Target.ThreadId != request.View.ThreadId)
                throw new InvalidOperationException("Request is no longer active.");
            if (_owned.TurnId is not null && request.View.TurnId != _owned.TurnId) throw new InvalidOperationException("Request belongs to a previous turn.");
        }
        var response = GoalProtocol.Response(request.View, decision, answers);
        await _connection!.ReplyAsync(request.Id, response, false, token).ConfigureAwait(false);
        lock (_sync) _pending.Remove(key);
        if (HasActiveWork) Publish(ConversationResumeStatus.Resumed, L("已回覆請求，等待工作繼續。", "Response sent; waiting for work to continue."));
        else Changed?.Invoke();
        if (decision == "cancel") await PauseAsync(token).ConfigureAwait(false);
    }

    public async Task<bool> PauseAsync(CancellationToken token)
    {
        try { await _operation.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        try
        {
            OwnedGoal? owned; lock (_sync) owned = _owned;
            if (owned is null) return true;
            await ConnectAsync(token).ConfigureAwait(false);
            var goal = GoalProtocol.Property(await _connection!.RequestAsync("thread/goal/get", new { threadId = owned.Target.ThreadId }, token).ConfigureAwait(false), "goal");
            if (GoalProtocol.GoalFingerprint(goal) != owned.Fingerprint) { await FinishAsync("changed").ConfigureAwait(false); return true; }
            _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _connection.RequestAsync("thread/goal/set", new { threadId = owned.Target.ThreadId, status = "paused" }, token).ConfigureAwait(false);
            string? activeTurn;
            lock (_sync) activeTurn = _owned?.TurnId;
            if (activeTurn is null)
            {
                var thread = GoalProtocol.Property(await _connection.RequestAsync("thread/read", new { threadId = owned.Target.ThreadId, includeTurns = false }, token).ConfigureAwait(false), "thread");
                if (GoalProtocol.Status(GoalProtocol.Property(thread, "status")) == "active")
                    activeTurn = await FindActiveTurnAsync(owned.Target.ThreadId!, token).ConfigureAwait(false) ?? throw new InvalidOperationException("Active turn identity is unknown.");
            }
            if (activeTurn is not null)
            {
                await _connection.RequestAsync("turn/interrupt", new { threadId = owned.Target.ThreadId, turnId = activeTurn }, token).ConfigureAwait(false);
                await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
            await FinishAsync("paused").ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex) || ex is TimeoutException)
        { Publish(ConversationResumeStatus.PendingConfirmation, L("未能確認暫停；請重新檢查，尚未清除持有狀態。", "Pause is unconfirmed; ownership state is retained.")); return false; }
        finally { _operation.Release(); }
    }

    private async Task FinishAsync(string status)
    {
        _lastGoalStatus = status;
        var result = status == "complete" ? ConversationResumeStatus.Completed : status == "usageLimited" ? ConversationResumeStatus.WaitingForReset : ConversationResumeStatus.Paused;
        Publish(result, L($"背景 Goal 狀態：{status}", $"Background Goal status: {status}"));
        lock (_sync) { _owned = null; _pending.Clear(); _fileChanges.Clear(); _stopped?.TrySetResult(true); _started?.TrySetResult(false); }
        await PersistAsync().ConfigureAwait(false);
        ReleaseLease(); Changed?.Invoke();
    }

    private async Task<string?> FindActiveTurnAsync(string threadId, CancellationToken token)
    {
        var page = await _connection!.RequestAsync("thread/turns/list", new { threadId, limit = 1, sortDirection = "desc", itemsView = "notLoaded" }, token).ConfigureAwait(false);
        var data = GoalProtocol.Property(page, "data");
        return data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Where(x => GoalProtocol.Text(x, "status") == "inProgress").Select(x => GoalProtocol.Text(x, "id")).FirstOrDefault() : null;
    }

    private void AcquireLease(string threadId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        try
        {
            _globalLease = new FileStream(Path.Combine(Path.GetDirectoryName(_statePath)!, "background-goal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _lease = new FileStream(Path.Combine(Path.GetDirectoryName(_statePath)!, "goal-" + threadId + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { ReleaseLease(); throw new GoalLeaseBusyException(); }
    }
    private void ReleaseLease() { _lease?.Dispose(); _lease = null; _globalLease?.Dispose(); _globalLease = null; }

    private async Task PersistAsync()
    {
        await _persistence.WaitAsync().ConfigureAwait(false);
        try
        {
            OwnedGoal? value; lock (_sync) value = _owned;
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            if (value is null) { File.Delete(_statePath); return; }
            var temp = _statePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value), System.Text.Encoding.UTF8).ConfigureAwait(false);
            File.Move(temp, _statePath, true);
        }
        finally { _persistence.Release(); }
    }
    private void Publish(ConversationResumeStatus status, string message)
    {
        lock (_sync) State = new(_owned?.Target.ThreadId ?? State.ThreadId, _owned?.TurnId, status, message, _lastGoalStatus);
        Changed?.Invoke();
    }
    private static ConversationResumeResult Result(ConversationTarget target, ConversationResumeStatus status, string message) => new(target.ProjectName, target.Title, status, message, DateTimeOffset.Now);
    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
    public void Dispose() { _lifetime.Cancel(); ReleaseLease(); if (_connection is not null) _ = _connection.DisposeAsync(); }
    public async ValueTask DisposeAsync() { _lifetime.Cancel(); if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false); ReleaseLease(); }
}

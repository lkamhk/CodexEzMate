using System.IO;
using System.Text;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed class DesktopGoalResumeService : IGoalResumeService, IAsyncDisposable, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly Func<CancellationToken, Task> _ensureHost;
    private readonly Func<int, CancellationToken, Task<IGoalRpcConnection>> _connect;
    private readonly ITrayNotificationService _notifications;
    private readonly string _statePath;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IGoalRpcConnection? _connection;
    private FileStream? _lease;
    private QueueRecord? _record;
    private Task? _observer;
    private Task? _events;
    private bool _initialized, _recoveryBlocked;
    private volatile bool _exiting;
    private int _port;

    // Persist intent before side effects. Unknown outcomes are checked, never replayed.
    private sealed record QueueRecord(int Version, ConversationTarget Target, string Fingerprint, int Port, string Path,
        string RequestId, string Phase, string? QueueId = null, string? TurnId = null, bool Notified = false);
    public bool HasActiveWork => _record is not null || _recoveryBlocked;
    public GoalExecutionState State { get; private set; } = new(null, null, ConversationResumeStatus.Unknown, "");
    public IReadOnlyList<GoalPendingRequest> PendingRequests => [];
    public event Action? Changed;
    public event Action? UsageChanged;
    internal void PrepareToExit(bool value) => _exiting = value;

    public DesktopGoalResumeService(ISettingsService settings, CodexAppServerHost host, ITrayNotificationService notifications)
        : this(settings, async token =>
        {
            await host.StartAsync().ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
            while (host.State == AppServerHostState.Starting && DateTimeOffset.UtcNow < deadline) await Task.Delay(200, token).ConfigureAwait(false);
            if (host.State != AppServerHostState.Ready) throw new InvalidOperationException("App Server is not ready.");
        }, GoalRpcConnection.ConnectAsync, notifications,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageAssistant", "settings", "goal-runtime.json")) { }

    internal DesktopGoalResumeService(ISettingsService settings, Func<CancellationToken, Task> ensureHost,
        Func<int, CancellationToken, Task<IGoalRpcConnection>> connect, ITrayNotificationService notifications,
        string statePath, TimeSpan? pollInterval = null)
    { _settings = settings; _ensureHost = ensureHost; _connect = connect; _notifications = notifications; _statePath = statePath; _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3); }

    public Task<ConversationResumeResult> ResumeAsync(ConversationTarget target, CancellationToken token) => ResumeCoreAsync(target, false, token);
    public Task<ConversationResumeResult> ContinuePausedAsync(ConversationTarget target, CancellationToken token) => ResumeCoreAsync(target, true, token);

    internal async Task InitializeAsync(CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            _initialized = true;
            if (!File.Exists(_statePath)) return;
            AcquireLease();
            var saved = JsonSerializer.Deserialize<QueueRecord>(await File.ReadAllTextAsync(_statePath, token).ConfigureAwait(false));
            if (saved is not { Version: 2 } || saved.Target is null || !Guid.TryParse(saved.Target.ThreadId, out _) ||
                !Guid.TryParse(saved.RequestId, out _) || string.IsNullOrEmpty(saved.Fingerprint) || saved.Port is < 1 or > 65535 ||
                saved.Phase is not ("activating" or "queueing" or "queued" or "observing"))
                throw new InvalidDataException("Unrecognized recovery record.");
            _record = saved; _port = saved.Port;
            Publish(ConversationResumeStatus.PendingConfirmation, L("正在核對上次恢復；不重複發送。", "Checking the previous recovery without resending."));
            StartObserver();
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        {
            _recoveryBlocked = true;
            Publish(ConversationResumeStatus.PendingConfirmation, L("舊版或無法讀取的恢復記錄需要人工核對；未啟動工作。", "A legacy or unreadable recovery record needs manual review; no work was started."));
        }
        finally { _operation.Release(); }
    }

    public async Task<UsageData> ReadUsageAsync(CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ConnectAsync(token).ConfigureAwait(false);
            if (_record is not null) await ReconcileAsync(token).ConfigureAwait(false);
            var usage = await ReadQuotaAsync(token).ConfigureAwait(false);
            if (usage.Status == UsageStatus.Available)
            {
                try { AppServerUsageService.ApplyTokenUsage(usage, await _connection!.RequestAsync("account/usage/read", null, token).ConfigureAwait(false), DateTimeOffset.Now); }
                catch (HostRpcException) { }
                await LocalTokenUsageService.EnrichTodayAsync(usage, token).ConfigureAwait(false);
            }
            return usage;
        }
        finally { _operation.Release(); }
    }
    private async Task<UsageData> ReadQuotaAsync(CancellationToken token) =>
        AppServerUsageService.ParseRateLimits(await _connection!.RequestAsync("account/rateLimits/read", null, token).ConfigureAwait(false), DateTimeOffset.Now);

    private async Task<ConversationResumeResult> ResumeCoreAsync(ConversationTarget target, bool manual, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_exiting) return Result(target, ConversationResumeStatus.NoActionNeeded, L("程式正在退出。", "The app is exiting."));
            if (!manual && !target.Enabled) return Result(target, ConversationResumeStatus.NoActionNeeded, L("此對話未勾選監控。", "This conversation is not selected for monitoring."));
            if (HasActiveWork) return Result(target, ConversationResumeStatus.PendingConfirmation, L("已有待確認或正在追蹤的恢復，不重複提交。", "A recovery is already pending or being tracked; no duplicate was sent."));
            if (!Guid.TryParse(target.ThreadId, out _)) return Result(target, ConversationResumeStatus.NotFound, L("請重新掃描有效的對話 ID。", "Rescan to obtain a valid conversation ID."));
            await ConnectAsync(token).ConfigureAwait(false);
            if (!GoalProtocol.UsageAllowsResume(await ReadQuotaAsync(token).ConfigureAwait(false)))
                return Result(target, ConversationResumeStatus.WaitingForReset, L("等待 App Server 確認額度可用。", "Waiting for App Server to confirm available quota."));
            var goal = await ReadGoalAsync(target.ThreadId!, token).ConfigureAwait(false);
            target.GoalStatus = GoalProtocol.Text(goal, "status");
            if (!GoalProtocol.CanResumeGoal(target.GoalStatus, manual)) return Result(target, ConversationResumeStatus.NoActionNeeded,
                L("只自動恢復因額度停止的 Goal；人工繼續另允許 paused。", "Only usage-limited Goals resume automatically; explicit continuation also permits paused."));
            var thread = GoalProtocol.Property(await _connection!.RequestAsync("thread/read", new { threadId = target.ThreadId, includeTurns = false }, token).ConfigureAwait(false), "thread");
            target.Cwd = GoalProtocol.Text(thread, "cwd"); var path = GoalProtocol.Text(thread, "path");
            if (GoalProtocol.InternalSource(GoalProtocol.Property(thread, "source")) || GoalProtocol.Property(thread, "ephemeral").ValueKind == JsonValueKind.True ||
                GoalProtocol.Text(thread, "source") != "vscode" || GoalProtocol.Text(thread, "historyMode") != "paginated" ||
                string.IsNullOrWhiteSpace(target.Cwd) || target.Cwd.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(target.Cwd) || path is null)
                return Result(target, ConversationResumeStatus.Unsupported, L("目前只驗證本機 Desktop 的已保存對話；CLI／其他客戶端尚未支援。", "Only saved local Desktop conversations are currently verified; CLI and other clients are not supported yet."));
            var observation = await GoalLocalObservation.ReadAsync(path, target.ThreadId!, null, null, token).ConfigureAwait(false);
            if (!observation.HasLifecycle || observation.IsBusy || GoalProtocol.Status(GoalProtocol.Property(thread, "status")) == "active")
                return Result(target, ConversationResumeStatus.Busy, L("對話正在執行或狀態未明；未發送恢復訊息。", "The conversation is running or its state is unknown; no recovery message was sent."));
            if ((await ReadQueueAsync(target.ThreadId!, token).ConfigureAwait(false)).Count != 0)
                return Result(target, ConversationResumeStatus.Busy, L("原對話已有排隊訊息，請先在 Desktop 處理。", "The conversation already has queued input; handle it in Desktop first."));
            AcquireLease();
            var current = await ReadGoalAsync(target.ThreadId!, token).ConfigureAwait(false);
            if (GoalProtocol.GoalFingerprint(current) != GoalProtocol.GoalFingerprint(goal) || !GoalProtocol.CanResumeGoal(GoalProtocol.Text(current, "status"), manual))
            { ReleaseLease(); return Result(target, ConversationResumeStatus.NoActionNeeded, L("Goal 狀態已改變。", "The Goal state changed.")); }
            observation = await GoalLocalObservation.ReadAsync(path, target.ThreadId!, null, null, token).ConfigureAwait(false);
            if (observation.IsBusy || !observation.HasLifecycle) { ReleaseLease(); return Result(target, ConversationResumeStatus.Busy, L("對話已開始執行。", "The conversation has started running.")); }
            if (!GoalProtocol.UsageAllowsResume(await ReadQuotaAsync(token).ConfigureAwait(false)))
            { ReleaseLease(); return Result(target, ConversationResumeStatus.WaitingForReset, L("額度仍不可用。", "Quota is not available.")); }
            var requestId = Guid.NewGuid().ToString();
            var fingerprint = GoalProtocol.GoalFingerprint(current);
            // Prime the append cursor before sending, so a fast large response cannot hide the receipt.
            var primed = await GoalLocalObservation.ReadAsync(path, target.ThreadId!, requestId, fingerprint, token).ConfigureAwait(false);
            if (primed.IsBusy) { ReleaseLease(); return Result(target, ConversationResumeStatus.Busy, L("對話已開始執行。", "The conversation has started running.")); }
            _record = new(2, target, fingerprint, _port, path, requestId, "activating");
            await PersistAsync().ConfigureAwait(false);
            Publish(ConversationResumeStatus.Resuming, L("正在恢復原 Goal 狀態…", "Restoring the original Goal state…"));
            var activated = GoalProtocol.Property(await _connection.RequestAsync("thread/goal/set", new { threadId = target.ThreadId, status = "active" }, token).ConfigureAwait(false), "goal");
            if (GoalProtocol.GoalFingerprint(activated) != _record.Fingerprint || GoalProtocol.Text(activated, "status") != "active")
                throw new InvalidDataException("Activation changed Goal identity.");
            foreach (var field in new[] { "tokenBudget", "tokensUsed", "timeUsedSeconds" })
                if (GoalProtocol.Property(activated, field).ToString() != GoalProtocol.Property(current, field).ToString())
                    throw new InvalidDataException("Activation changed accounting.");
            target.GoalStatus = "active";
            // Another client may have acted during activation; never append another message then.
            observation = await GoalLocalObservation.ReadAsync(path, target.ThreadId!, null, null, token).ConfigureAwait(false);
            current = await ReadGoalAsync(target.ThreadId!, token).ConfigureAwait(false);
            if (observation.IsBusy || GoalProtocol.GoalFingerprint(current) != _record.Fingerprint || GoalProtocol.Text(current, "status") != "active" ||
                (await ReadQueueAsync(target.ThreadId!, token).ConfigureAwait(false)).Count != 0)
            {
                _record = _record with { Phase = "observing" }; await PersistAsync().ConfigureAwait(false);
                Publish(ConversationResumeStatus.PendingConfirmation, L("原對話已變更，沒有額外排入訊息；正在核對。", "The original conversation changed; no extra message was queued. Checking."));
            }
            else
            {
                _record = _record with { Phase = "queueing" }; await PersistAsync().ConfigureAwait(false);
                var response = await _connection.RequestAsync("thread/queue/add", new
                {
                    threadId = target.ThreadId, clientUserMessageId = _record.RequestId,
                    input = new[] { new { type = "text", text = $"[Codex EzMate {_record.RequestId}]\n" +
                        L("請先檢查原 Goal。只有原 Goal 仍為 active 時，才按原目標、原預算及原批准規則繼續；不要建立新 Goal。若已暫停、完成、等待輸入或狀態不符，請停止並說明，不要自行重新啟用。",
                          "Check the existing Goal first. Continue its original objective, budget and approval rules only if it is still active. Do not create a new Goal. If it is paused, completed, waiting for input, or otherwise incompatible, stop and explain without reactivating it.") } }
                }, token).ConfigureAwait(false);
                _record = _record with { Phase = "queued", QueueId = GoalProtocol.Text(GoalProtocol.Property(response, "queuedSubmission"), "id") };
                await PersistAsync().ConfigureAwait(false);
                target.Compatibility = L("由原 Desktop 執行", "Runs in the original Desktop");
                Publish(ConversationResumeStatus.Queued, L("已排入恢復訊息，等待原 Desktop 執行；請保持 Desktop 開啟。", "Recovery message queued; waiting for the original Desktop. Keep Desktop open."));
            }
            StartObserver(); return Result(target, State.Status, State.Message);
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        {
            if (_record is not null)
            {
                Publish(ConversationResumeStatus.PendingConfirmation, L("恢復結果未明，正在核對；不會自動重送。", "Recovery outcome is unknown; checking without automatic resubmission."));
                StartObserver(); return Result(target, State.Status, State.Message);
            }
            ReleaseLease();
            return Result(target, ex is HostRpcException { Code: -32601 } ? ConversationResumeStatus.Unsupported : ConversationResumeStatus.Failed,
                ex is HostRpcException rpc ? L($"App Server 未接受 {rpc.Method}（{rpc.Code}）。", $"App Server rejected {rpc.Method} ({rpc.Code}).") :
                L("未能安全確認原對話狀態；沒有發送恢復訊息。", "Could not verify the original conversation safely; no recovery message was sent."));
        }
        finally { if (_record is null && !_recoveryBlocked) ReleaseLease(); _operation.Release(); }
    }

    private async Task<JsonElement> ReadGoalAsync(string id, CancellationToken token) =>
        GoalProtocol.Property(await _connection!.RequestAsync("thread/goal/get", new { threadId = id }, token).ConfigureAwait(false), "goal");
    private async Task<List<JsonElement>> ReadQueueAsync(string id, CancellationToken token)
    {
        var rows = new List<JsonElement>(); var cursors = new HashSet<string>(); string? cursor = null;
        do
        {
            var response = await _connection!.RequestAsync("thread/queue/list", new { threadId = id, cursor, limit = 100 }, token).ConfigureAwait(false);
            var data = GoalProtocol.Property(response, "data");
            if (data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Queue unavailable.");
            rows.AddRange(data.EnumerateArray().Select(x => x.Clone())); cursor = GoalProtocol.Text(response, "nextCursor");
            if (cursor is not null && (!cursors.Add(cursor) || cursors.Count > 20)) throw new InvalidDataException("Invalid queue pagination.");
        } while (cursor is not null);
        return rows;
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        if (_connection?.IsConnected == true) return;
        _port = _record?.Port ?? (await _settings.LoadAsync(token).ConfigureAwait(false))?.HostPort ?? 4500;
        await _ensureHost(token).ConfigureAwait(false);
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = await _connect(_port, token).ConfigureAwait(false); var connection = _connection;
        _events = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in connection.Events(_lifetime.Token).ConfigureAwait(false))
                {
                    if (GoalProtocol.Text(message, "method") == "account/rateLimits/updated") UsageChanged?.Invoke();
                    // Execution and approvals belong to the original client, never EzMate.
                    if (message.TryGetProperty("id", out var id)) await connection.ReplyAsync(id, null, true, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (CodexAppServerHost.IsExpected(ex)) { }
        });
    }

    private void StartObserver()
    {
        if (_observer is { IsCompleted: false }) return;
        _observer = Task.Run(async () =>
        {
            try
            {
                while (!_lifetime.IsCancellationRequested && !_exiting && _record is not null)
                {
                    await Task.Delay(_pollInterval, _lifetime.Token).ConfigureAwait(false);
                    if (_exiting) break;
                    await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    try { await ConnectAsync(_lifetime.Token).ConfigureAwait(false); await ReconcileAsync(_lifetime.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
                    { Publish(ConversationResumeStatus.PendingConfirmation, L("暫時無法核對原對話；保留紀錄，不重複提交。", "Cannot check the original conversation; retaining the record without resubmission.")); }
                    finally { _operation.Release(); }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    internal async Task ReconcileAsync(CancellationToken token)
    {
        var record = _record; if (record is null) return;
        var id = record.Target.ThreadId!;
        var observation = await GoalLocalObservation.ReadAsync(record.Path, id, record.RequestId, record.Fingerprint, token).ConfigureAwait(false);
        var goal = await ReadGoalAsync(id, token).ConfigureAwait(false);
        var same = goal.ValueKind == JsonValueKind.Object && GoalProtocol.GoalFingerprint(goal) == record.Fingerprint;
        var status = same ? GoalProtocol.Text(goal, "status") : goal.ValueKind == JsonValueKind.Object ? "changed" : observation.GoalStatus;
        record.Target.GoalStatus = status ?? "unknown";
        var ownQueue = (await ReadQueueAsync(id, token).ConfigureAwait(false)).FirstOrDefault(x => GoalProtocol.Text(x, "clientUserMessageId") == record.RequestId);
        if (ownQueue.ValueKind == JsonValueKind.Object)
        {
            if (!same || status != "active")
            {
                await _connection!.RequestAsync("thread/queue/delete", new { threadId = id, queuedSubmissionId = GoalProtocol.Text(ownQueue, "id") }, token).ConfigureAwait(false);
                await FinishAsync(status, L("原 Goal 已變更，已取消尚未執行的恢復訊息。", "The Goal changed; the pending recovery message was cancelled.")).ConfigureAwait(false); return;
            }
            Publish(ConversationResumeStatus.Queued, L("訊息仍在佇列，等待原 Desktop；不會重送。", "The message is still queued for the original Desktop; it will not be resent.")); return;
        }
        var seen = observation.RequestSeen || record.TurnId is not null;
        if (seen && (status is "complete" or "paused" or "usageLimited" or "budgetLimited" or "blocked" || !same && !observation.IsBusy))
        { await FinishAsync(status, L("原 Goal 已停止；請在原 Desktop 查看結果。", "The original Goal has stopped; review its result in Desktop.")).ConfigureAwait(false); return; }
        if (seen)
        {
            _record = record with { Phase = "observing", TurnId = observation.TurnId ?? record.TurnId, Notified = true };
            await PersistAsync().ConfigureAwait(false);
            Publish(ConversationResumeStatus.Resumed, L("已確認原 Desktop 收到訊息並開始續跑；批准及進度請在原視窗查看。", "Confirmed the original Desktop started continuing; review progress and approvals there."));
            if (!record.Notified) _notifications.ShowNotification(L("Goal 已恢復", "Goal resumed"), record.Target.Title);
        }
        else if (record.Phase == "observing" && (status is "complete" or "paused" or "usageLimited" or "budgetLimited" or "blocked" || !same && !observation.IsBusy))
            await FinishAsync(status, L("原對話已自行處理，沒有額外發送訊息。", "The original conversation handled the change; no extra message was sent.")).ConfigureAwait(false);
        else Publish(ConversationResumeStatus.PendingConfirmation, L("尚未找到本次訊息的執行證據；不重送。可取消等待後在原 Desktop 處理。", "No execution evidence for this request yet; not resending. Cancel waiting and handle it in Desktop if needed."));
    }

    public Task RespondAsync(string key, string decision, IReadOnlyDictionary<string, string[]>? answers, CancellationToken token) =>
        Task.FromException(new InvalidOperationException("Review approvals in the original Desktop."));

    public async Task<bool> PauseAsync(CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_recoveryBlocked) { Publish(ConversationResumeStatus.PendingConfirmation, L("請先核對舊版恢復記錄與原工作；不自動清除。", "Reconcile the legacy record and original work first; it is not cleared automatically.")); return false; }
            var record = _record; if (record is null) return true;
            await ConnectAsync(token).ConfigureAwait(false);
            var goal = await ReadGoalAsync(record.Target.ThreadId!, token).ConfigureAwait(false);
            if (goal.ValueKind == JsonValueKind.Object && GoalProtocol.GoalFingerprint(goal) == record.Fingerprint)
                await _connection!.RequestAsync("thread/goal/set", new { threadId = record.Target.ThreadId, status = "paused" }, token).ConfigureAwait(false);
            foreach (var row in await ReadQueueAsync(record.Target.ThreadId!, token).ConfigureAwait(false))
                if (GoalProtocol.Text(row, "clientUserMessageId") == record.RequestId)
                    await _connection!.RequestAsync("thread/queue/delete", new { threadId = record.Target.ThreadId, queuedSubmissionId = GoalProtocol.Text(row, "id") }, token).ConfigureAwait(false);
            await FinishAsync("paused", L("已取消待處理恢復並暫停 Goal 後續續跑；已開始的回合請在原 Desktop 停止。", "Pending recovery cancelled and further Goal continuation paused; stop any running turn in Desktop.")).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        { Publish(ConversationResumeStatus.PendingConfirmation, L("取消結果未明，保留恢復紀錄。", "Cancellation is unconfirmed; the recovery record is retained.")); return false; }
        finally { _operation.Release(); }
    }

    private Task FinishAsync(string? status, string message)
    {
        var record = _record!;
        var result = status == "complete" ? ConversationResumeStatus.Completed : status == "usageLimited" ? ConversationResumeStatus.WaitingForReset :
            status is "paused" or "budgetLimited" or "blocked" ? ConversationResumeStatus.Paused : ConversationResumeStatus.NoActionNeeded;
        File.Delete(_statePath);
        GoalLocalObservation.Forget(record.Path);
        Publish(result, message, status ?? "unknown");
        _record = null; ReleaseLease(); Changed?.Invoke();
        if (result == ConversationResumeStatus.Completed) _notifications.ShowNotification(L("Goal 已完成", "Goal completed"), record.Target.Title);
        return Task.CompletedTask;
    }
    private void AcquireLease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        _lease ??= new FileStream(Path.Combine(Path.GetDirectoryName(_statePath)!, "background-goal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void ReleaseLease() { _lease?.Dispose(); _lease = null; }
    private async Task PersistAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temp = _statePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_record), new UTF8Encoding(false)).ConfigureAwait(false);
        File.Move(temp, _statePath, true);
    }
    private void Publish(ConversationResumeStatus status, string message, string? goalStatus = null)
    {
        var next = new GoalExecutionState(_record?.Target.ThreadId ?? State.ThreadId, _record?.TurnId, status, message,
            goalStatus ?? _record?.Target.GoalStatus ?? State.GoalStatus);
        if (State == next) return;
        State = next; Changed?.Invoke();
    }
    private static ConversationResumeResult Result(ConversationTarget target, ConversationResumeStatus status, string message) => new(target.ProjectName, target.Title, status, message, DateTimeOffset.Now);
    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
    public void Dispose() { _exiting = true; _lifetime.Cancel(); ReleaseLease(); if (_connection is not null) _ = _connection.DisposeAsync(); }
    public async ValueTask DisposeAsync()
    {
        _exiting = true; _lifetime.Cancel();
        if (_observer is not null) await _observer.ConfigureAwait(false);
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        if (_events is not null) await _events.ConfigureAwait(false);
        ReleaseLease();
    }
}

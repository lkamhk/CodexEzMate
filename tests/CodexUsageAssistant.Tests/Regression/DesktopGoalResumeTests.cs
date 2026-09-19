using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class DesktopGoalResumeTests
{
    private const string Id = "01900000-0000-7000-8000-000000000002";
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
    private sealed class Options(int port = 4500) : ISettingsService
    {
        public Task<WindowPosition?> LoadAsync(CancellationToken token) => Task.FromResult<WindowPosition?>(new() { HostPort = port });
        public Task SaveAsync(WindowPosition settings, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Notices : ITrayNotificationService
    {
        public List<string> Titles = [];
        public void ShowNotification(string title, string message) => Titles.Add(title);
    }
    private sealed class Backend(string root)
    {
        public string Path = System.IO.Path.Combine(root, "rollout-" + Id + ".jsonl");
        public string? Status = "usageLimited";
        public string Source = "vscode", History = "paginated", ThreadStatus = "notLoaded";
        public long Tokens = 100, Created = 1;
        public bool Allowed = true, LoseActivationReply, LoseQueueReply, FailQueueSupport;
        public Action? AfterActivation;
        public Action<int>? OnGoalRead;
        private int _goalReads;
        public List<(string Method, JsonElement Parameters)> Calls = [];
        public List<JsonElement> Queue = [];
        public object? Goal => Status is null ? null : new { threadId = Id, objective = "Keep this existing Goal", createdAt = Created, status = Status, tokenBudget = 10000, tokensUsed = Tokens, timeUsedSeconds = 4 };
        public void Append(object value) => File.AppendAllText(Path, JsonSerializer.Serialize(value) + "\n", System.Text.Encoding.UTF8);
        public void Idle() => Append(new { type = "event_msg", payload = new { type = "task_complete", turn_id = "baseline" } });
        public void Start(string turn = "active") => Append(new { type = "event_msg", payload = new { type = "task_started", turn_id = turn } });
        public void Deliver()
        {
            var row = Queue.First(); var text = row.GetProperty("input")[0].GetProperty("text").GetString();
            Start("delivered");
            Append(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text } },
                internal_chat_message_metadata_passthrough = new { turn_id = "delivered" } } });
            Queue.RemoveAt(0);
        }
        public void Complete()
        {
            Status = "complete"; Tokens += 25;
            Append(new { type = "response_item", payload = new { type = "custom_tool_call", name = "exec", call_id = "complete", input = "text(await tools.update_goal({status: 'complete'}));" } });
            Append(new { type = "response_item", payload = new { type = "custom_tool_call_output", call_id = "complete", output = new[] { new { type = "input_text", text = JsonSerializer.Serialize(new { goal = Goal }) } } } });
            Append(new { type = "event_msg", payload = new { type = "task_complete", turn_id = "delivered" } });
            Status = null;
        }
        public JsonElement Request(string method, object? parameters)
        {
            var p = Json(parameters); Calls.Add((method, p));
            switch (method)
            {
                case "account/rateLimits/read": return Json(new { ordinaryUsageAllowed = Allowed, rateLimits = new { secondary = new { usedPercent = Allowed ? 10 : 100, windowDurationMins = 10080 } } });
                case "account/usage/read": return Json(new { });
                case "thread/goal/get": OnGoalRead?.Invoke(++_goalReads); return Json(new { goal = Goal });
                case "thread/read": return Json(new { thread = new { id = Id, cwd = root, path = Path, source = Source, historyMode = History, status = new { type = ThreadStatus } } });
                case "thread/queue/list":
                    if (FailQueueSupport) throw new HostRpcException(-32601).ForMethod(method);
                    return Json(new { data = Queue.ToArray(), nextCursor = (string?)null });
                case "thread/goal/set":
                    Status = p.GetProperty("status").GetString(); AfterActivation?.Invoke();
                    if (LoseActivationReply) { LoseActivationReply = false; throw new IOException("Disconnected after activation"); }
                    return Json(new { goal = Goal });
                case "thread/queue/add":
                    var row = Json(new { id = "queue-" + Queue.Count, clientUserMessageId = p.GetProperty("clientUserMessageId").GetString(), input = p.GetProperty("input") });
                    Queue.Add(row);
                    if (LoseQueueReply) { LoseQueueReply = false; throw new IOException("Disconnected after enqueue"); }
                    return Json(new { queuedSubmission = row });
                case "thread/queue/delete": Queue.RemoveAll(x => x.GetProperty("id").GetString() == p.GetProperty("queuedSubmissionId").GetString()); return Json(new { });
                default: throw new InvalidOperationException("Unexpected RPC: " + method);
            }
        }
        public Task<IGoalRpcConnection> Connect(int port, CancellationToken token) => Task.FromResult<IGoalRpcConnection>(new Connection(this));
    }
    private sealed class Connection(Backend backend) : IGoalRpcConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public bool IsConnected { get; private set; } = true;
        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token) => Task.FromResult(backend.Request(method, parameters));
        public Task ReplyAsync(JsonElement id, object? result, bool error, CancellationToken token) => Task.CompletedTask;
        public IAsyncEnumerable<JsonElement> Events(CancellationToken token) => _events.Reader.ReadAllAsync(token);
        public ValueTask DisposeAsync() { IsConnected = false; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ezmate-desktop-" + Guid.NewGuid().ToString("N"));
        public string StatePath => System.IO.Path.Combine(Root, "settings", "goal-runtime.json");
        public Backend Backend;
        public Notices Notices = new();
        public DesktopGoalResumeService Service;
        public ConversationTarget Target = new() { ThreadId = Id, Title = "Isolated", Enabled = true };
        public Fixture() { Directory.CreateDirectory(Root); Backend = new(Root); Backend.Idle(); Service = NewService(); }
        public DesktopGoalResumeService NewService() => new(new Options(), _ => Task.CompletedTask, Backend.Connect, Notices, StatePath, TimeSpan.FromHours(1));
        public async Task Restart() { await Service.DisposeAsync(); Service = NewService(); await Service.InitializeAsync(default); }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task QueuedThenObservedThenCompleted_DoesNotTakeOwnership()
    {
        await using var f = new Fixture();
        var result = await f.Service.ResumeAsync(f.Target, default);
        Assert.Equal(ConversationResumeStatus.Queued, result.Status);
        Assert.True(f.Service.HasActiveWork); Assert.Empty(f.Notices.Titles);
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method is "thread/resume" or "turn/start");
        var set = Assert.Single(f.Backend.Calls, x => x.Method == "thread/goal/set");
        Assert.Equal(new[] { "threadId", "status" }, set.Parameters.EnumerateObject().Select(x => x.Name));
        f.Backend.Deliver(); await f.Service.ReadUsageAsync(default);
        Assert.Equal(ConversationResumeStatus.Resumed, f.Service.State.Status); Assert.Single(f.Notices.Titles);
        await f.Service.ReadUsageAsync(default); Assert.Single(f.Notices.Titles);
        f.Backend.Complete(); await f.Service.ReadUsageAsync(default);
        Assert.Equal(ConversationResumeStatus.Completed, f.Service.State.Status);
        Assert.False(f.Service.HasActiveWork); Assert.False(File.Exists(f.StatePath)); Assert.Equal(2, f.Notices.Titles.Count);
    }

    [Theory]
    [InlineData("paused")][InlineData("active")][InlineData("complete")][InlineData("blocked")][InlineData("budgetLimited")][InlineData(null)]
    public async Task AutomaticResume_SkipsNonUsageLimitedStates(string? status)
    {
        await using var f = new Fixture(); f.Backend.Status = status;
        Assert.Equal(ConversationResumeStatus.NoActionNeeded, (await f.Service.ResumeAsync(f.Target, default)).Status);
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method is "thread/goal/set" or "thread/queue/add");
    }

    [Fact]
    public async Task ExplicitContinueAllowsPausedButUnselectedAutomaticDoesNothing()
    {
        await using var f = new Fixture(); f.Target.Enabled = false;
        Assert.Equal(ConversationResumeStatus.NoActionNeeded, (await f.Service.ResumeAsync(f.Target, default)).Status);
        f.Backend.Status = "paused";
        Assert.Equal(ConversationResumeStatus.Queued, (await f.Service.ContinuePausedAsync(f.Target, default)).Status);
    }

    [Fact]
    public async Task BusyOriginalClientBlockedEvenWhenSeparateServerSaysNotLoaded()
    {
        await using var f = new Fixture(); f.Backend.Start();
        Assert.Equal(ConversationResumeStatus.Busy, (await f.Service.ResumeAsync(f.Target, default)).Status);
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method == "thread/goal/set");
    }

    [Fact]
    public async Task ExistingQueuedInputIsNotOvertaken()
    {
        await using var f = new Fixture(); f.Backend.Queue.Add(Json(new { id = "user", clientUserMessageId = "user" }));
        Assert.Equal(ConversationResumeStatus.Busy, (await f.Service.ResumeAsync(f.Target, default)).Status);
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method == "thread/goal/set");
    }

    [Fact]
    public async Task QuotaAndUnsupportedClientOrQueueApi_DoNotMutateGoal()
    {
        await using var f = new Fixture(); f.Backend.Allowed = false;
        Assert.Equal(ConversationResumeStatus.WaitingForReset, (await f.Service.ResumeAsync(f.Target, default)).Status);
        f.Backend.Allowed = true; f.Backend.Source = "cli";
        Assert.Equal(ConversationResumeStatus.Unsupported, (await f.Service.ResumeAsync(f.Target, default)).Status);
        f.Backend.Source = "vscode"; f.Backend.FailQueueSupport = true;
        Assert.Equal(ConversationResumeStatus.Unsupported, (await f.Service.ResumeAsync(f.Target, default)).Status);
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method == "thread/goal/set");
    }

    [Fact]
    public async Task ConcurrentTriggersSendOneActivationAndOneMessage()
    {
        await using var f = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.Service.ResumeAsync(f.Target, default)));
        Assert.Single(f.Backend.Calls, x => x.Method == "thread/goal/set");
        Assert.Single(f.Backend.Calls, x => x.Method == "thread/queue/add");
    }

    [Fact]
    public async Task LostActivationReplyAndRestart_NeverEnqueueOrReactivate()
    {
        await using var f = new Fixture(); f.Backend.LoseActivationReply = true;
        Assert.Equal(ConversationResumeStatus.PendingConfirmation, (await f.Service.ResumeAsync(f.Target, default)).Status);
        await f.Restart(); await f.Service.ReadUsageAsync(default); await f.Service.ResumeAsync(f.Target, default);
        Assert.Single(f.Backend.Calls, x => x.Method == "thread/goal/set");
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method == "thread/queue/add");
        Assert.True(f.Service.HasActiveWork);
    }

    [Fact]
    public async Task LostEnqueueReplyAndRestart_ReconcilesWithoutResending()
    {
        await using var f = new Fixture(); f.Backend.LoseQueueReply = true;
        await f.Service.ResumeAsync(f.Target, default); await f.Restart(); await f.Service.ReadUsageAsync(default);
        Assert.Equal(ConversationResumeStatus.Queued, f.Service.State.Status);
        f.Backend.Deliver(); await f.Service.ReadUsageAsync(default);
        Assert.Equal(ConversationResumeStatus.Resumed, f.Service.State.Status);
        await f.Restart(); await f.Service.ReadUsageAsync(default);
        Assert.Single(f.Notices.Titles);
        Assert.Single(f.Backend.Calls, x => x.Method == "thread/queue/add");
        Assert.Single(f.Backend.Calls, x => x.Method == "thread/goal/set");
    }

    [Fact]
    public async Task QueueDisappearingWithoutReceipt_IsNotReportedAsRunning()
    {
        await using var f = new Fixture(); await f.Service.ResumeAsync(f.Target, default);
        f.Backend.Queue.Clear(); await f.Service.ReadUsageAsync(default);
        Assert.Equal(ConversationResumeStatus.PendingConfirmation, f.Service.State.Status);
        Assert.Empty(f.Notices.Titles); Assert.True(f.Service.HasActiveWork);
        Assert.Single(f.Backend.Calls, x => x.Method == "thread/queue/add");
    }

    [Fact]
    public async Task MissingGoalWithoutCompletionProof_IsNotReportedAsCompleted()
    {
        await using var f = new Fixture(); await f.Service.ResumeAsync(f.Target, default);
        f.Backend.Deliver(); await f.Service.ReadUsageAsync(default);
        f.Backend.Status = null;
        f.Backend.Append(new { type = "event_msg", payload = new { type = "task_complete", turn_id = "delivered" } });
        await f.Service.ReadUsageAsync(default);
        Assert.Equal(ConversationResumeStatus.NoActionNeeded, f.Service.State.Status);
        Assert.Single(f.Notices.Titles); Assert.False(f.Service.HasActiveWork);
    }

    [Fact]
    public async Task DisposalLeavesOriginalDesktopGoalRunningAndJournalIntact()
    {
        await using var f = new Fixture(); await f.Service.ResumeAsync(f.Target, default);
        await f.Service.DisposeAsync();
        Assert.Equal("active", f.Backend.Status); Assert.True(File.Exists(f.StatePath));
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method == "turn/interrupt");
    }

    [Fact]
    public async Task ExplicitCancellationDeletesOnlyOurQueueAndDoesNotInterruptClient()
    {
        await using var f = new Fixture(); await f.Service.ResumeAsync(f.Target, default);
        f.Backend.Queue.Add(Json(new { id = "user", clientUserMessageId = "user" }));
        Assert.True(await f.Service.PauseAsync(default));
        Assert.Equal("paused", f.Backend.Status); Assert.False(f.Service.HasActiveWork);
        Assert.Equal("user", Assert.Single(f.Backend.Queue).GetProperty("id").GetString());
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method == "turn/interrupt");
    }

    [Theory]
    [InlineData("paused")][InlineData("complete")][InlineData(null)]
    public async Task ChangedGoalCancelsOnlyOurUnconsumedMessage(string? status)
    {
        await using var f = new Fixture(); await f.Service.ResumeAsync(f.Target, default);
        f.Backend.Status = status; await f.Service.ReadUsageAsync(default);
        Assert.Empty(f.Backend.Queue); Assert.False(f.Service.HasActiveWork);
    }

    [Fact]
    public async Task OriginalClientStartsDuringActivation_NoExtraMessage()
    {
        await using var f = new Fixture(); f.Backend.AfterActivation = () => f.Backend.Start();
        Assert.Equal(ConversationResumeStatus.PendingConfirmation, (await f.Service.ResumeAsync(f.Target, default)).Status);
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method == "thread/queue/add");
    }

    [Fact]
    public async Task GoalChangedBeforeActivation_IsNotOverwritten()
    {
        await using var f = new Fixture(); f.Backend.OnGoalRead = count => { if (count == 2) f.Backend.Created = 2; };
        Assert.Equal(ConversationResumeStatus.NoActionNeeded, (await f.Service.ResumeAsync(f.Target, default)).Status);
        Assert.DoesNotContain(f.Backend.Calls, x => x.Method == "thread/goal/set");
    }

    [Theory]
    [InlineData("{bad")][InlineData("{\"Target\":{\"ThreadId\":\"legacy\"}}")]
    public async Task InvalidOrLegacyJournalFailsClosed(string journal)
    {
        await using var f = new Fixture(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f.StatePath)!); File.WriteAllText(f.StatePath, journal);
        Assert.Equal(ConversationResumeStatus.PendingConfirmation, (await f.Service.ResumeAsync(f.Target, default)).Status);
        Assert.Empty(f.Backend.Calls); Assert.True(File.Exists(f.StatePath));
    }

    [Fact]
    public async Task LiveDesktop_WhenExplicitlyEnabled_UsesOnlyDesignatedPausedTestGoal()
    {
        if (Environment.GetEnvironmentVariable("CODEX_TEST_DESKTOP_QUEUE") != "1") return;
        var liveId = Environment.GetEnvironmentVariable("CODEX_TEST_DESKTOP_QUEUE_THREAD");
        Assert.True(Guid.TryParse(liveId, out _), "Set the explicit isolated test thread ID before enabling the live test.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ezmate-live-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var process = new AppServerHostRuntime().Start(new WindowPosition
        {
            HostPort = port, HostExecutablePath = AppServerUsageService.FindExecutable(null),
            HostWorkingDirectory = System.IO.Path.Combine(root, "work")
        });
        IGoalRpcConnection? connected = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { connected = await GoalRpcConnection.ConnectAsync(port, timeout.Token); break; }
            catch (System.Net.WebSockets.WebSocketException) { await Task.Delay(250, timeout.Token); }
        }
        Assert.NotNull(connected);
        await using var inspection = connected;
        var goal = GoalProtocol.Property(await inspection.RequestAsync("thread/goal/get", new { threadId = liveId }, timeout.Token), "goal");
        Assert.Equal("paused", GoalProtocol.Text(goal, "status"));
        Assert.StartsWith("EZMATE_PRODUCT_20260918:", (GoalProtocol.Text(goal, "objective") ?? "").Replace('：', ':'));
        var notifications = new Notices();
        await using var service = new DesktopGoalResumeService(new Options(port), _ => Task.CompletedTask, GoalRpcConnection.ConnectAsync,
            notifications, System.IO.Path.Combine(root, "settings", "goal-runtime.json"), TimeSpan.FromSeconds(1));
        try
        {
            var result = await service.ContinuePausedAsync(new() { ThreadId = liveId, Title = "EzMate isolated Desktop test", Enabled = true }, timeout.Token);
            Assert.Equal(ConversationResumeStatus.Queued, result.Status);
            while (service.HasActiveWork) await Task.Delay(250, timeout.Token);
            Assert.Equal(ConversationResumeStatus.Completed, service.State.Status);
            Assert.NotEmpty(notifications.Titles);
        }
        finally
        {
            if (service.HasActiveWork)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await service.PauseAsync(cleanup.Token);
            }
            await service.DisposeAsync();
            process.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

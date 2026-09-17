using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Xunit;
using Xunit.Abstractions;

namespace CodexUsageAssistant.Tests;

public sealed class BackgroundGoalTests(ITestOutputHelper output)
{
    private const string Id = "01900000-0000-7000-8000-000000000001";
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private sealed class Options : ISettingsService
    {
        public Task<WindowPosition?> LoadAsync(CancellationToken token) => Task.FromResult<WindowPosition?>(new() { HostPort = 4500 });
        public Task SaveAsync(WindowPosition value, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Notifications : ITrayNotificationService
    {
        public int Count;
        public void ShowNotification(string title, string message) => Count++;
    }
    private sealed class Backend(string cwd)
    {
        public string GoalStatus = "usageLimited";
        public string ThreadStatus = "idle";
        public string Source = "cli";
        public bool Allowed = true;
        public bool StartEvents = true;
        public List<(string Method, JsonElement Parameters)> Calls = [];
        public List<(JsonElement Id, object? Result, bool Error)> Replies = [];
        public Connection? Latest;
        public object Goal => new { threadId = Id, objective = "Test existing Goal", createdAt = 1, status = GoalStatus, tokensUsed = 123, tokenBudget = 456, timeUsedSeconds = 5 };
        public Task<IGoalRpcConnection> Connect(int port, CancellationToken token) { Latest = new(this); return Task.FromResult<IGoalRpcConnection>(Latest); }
        public JsonElement Request(string method, object? parameters)
        {
            Calls.Add((method, Json(parameters!)));
            switch (method)
            {
                case "account/rateLimits/read": return Json(new { ordinaryUsageAllowed = Allowed, rateLimits = new { planType = "prolite", secondary = new { usedPercent = 4, windowDurationMins = 10080 } } });
                case "account/usage/read": return Json(new { });
                case "thread/goal/get": return Json(new { goal = Goal });
                case "thread/read":
                case "thread/resume": return Json(new { thread = new { id = Id, cwd, source = Source, ephemeral = false, status = new { type = ThreadStatus } } });
                case "thread/turns/list": return Json(new { data = new[] { new { id = "turn-1", status = ThreadStatus == "active" ? "inProgress" : "completed" } } });
                case "thread/goal/set":
                    GoalStatus = Calls[^1].Parameters.GetProperty("status").GetString()!;
                    Latest!.Emit(new { method = "thread/goal/updated", @params = new { threadId = Id, goal = Goal } });
                    if (GoalStatus == "active" && StartEvents)
                    {
                        ThreadStatus = "active";
                        Latest.Emit(new { method = "turn/started", @params = new { threadId = Id, turn = new { id = "turn-1", status = "inProgress" } } });
                    }
                    return Json(new { goal = Goal });
                case "turn/interrupt":
                    ThreadStatus = "idle";
                    Latest!.Emit(new { method = "turn/completed", @params = new { threadId = Id, turn = new { id = "turn-1", status = "interrupted" } } });
                    return Json(new { });
                default: throw new HostRpcException(-32601);
            }
        }
    }
    private sealed class Connection(Backend backend) : IGoalRpcConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public bool IsConnected { get; private set; } = true;
        public void Emit(object value) => _events.Writer.TryWrite(Json(value));
        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token) => Task.FromResult(backend.Request(method, parameters));
        public Task ReplyAsync(JsonElement id, object? result, bool error, CancellationToken token)
        { backend.Replies.Add((id, result, error)); return Task.CompletedTask; }
        public IAsyncEnumerable<JsonElement> Events(CancellationToken token) => _events.Reader.ReadAllAsync(token);
        public ValueTask DisposeAsync() { IsConnected = false; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "ezmate-goal-" + Guid.NewGuid().ToString("N"));
        public Backend Backend;
        public Notifications Notifications = new();
        public GoalResumeService Service;
        public ConversationTarget Target = new() { ThreadId = Id, Title = "Test Goal", Enabled = true };
        public Fixture()
        {
            Directory.CreateDirectory(Root); Backend = new(Root); Service = NewService();
        }
        public GoalResumeService NewService(TimeSpan? timeout = null) => new(new Options(), _ => Task.CompletedTask, Backend.Connect, Notifications, Path.Combine(Root, "settings", "goal-runtime.json"), timeout);
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Task.Delay(50);
            Directory.Delete(Root, true);
        }
    }
    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("vscode")]
    [InlineData("appServer")]
    public async Task BackgroundResume_UsesStableIdAndPreservesGoalAndPermissions(string source)
    {
        await using var fixture = new Fixture(); fixture.Backend.Source = source;
        var result = await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        Assert.Equal(ConversationResumeStatus.Resumed, result.Status);
        Assert.True(fixture.Service.HasActiveWork);
        var update = Assert.Single(fixture.Backend.Calls, x => x.Method == "thread/goal/set");
        Assert.Equal(new[] { "threadId", "status" }, update.Parameters.EnumerateObject().Select(x => x.Name));
        var resume = Assert.Single(fixture.Backend.Calls, x => x.Method == "thread/resume");
        Assert.False(resume.Parameters.TryGetProperty("model", out _));
        Assert.False(resume.Parameters.TryGetProperty("sandbox", out _));
        Assert.DoesNotContain(fixture.Backend.Calls, x => x.Method == "turn/start");
        Assert.True(await fixture.Service.PauseAsync(CancellationToken.None));
        Assert.Equal("paused", fixture.Backend.GoalStatus);
        Assert.False(fixture.Service.HasActiveWork);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("blocked")]
    [InlineData("budgetLimited")]
    [InlineData("complete")]
    [InlineData("active")]
    public async Task AutomaticResume_DoesNotReactivateOtherGoalStates(string state)
    {
        await using var fixture = new Fixture(); fixture.Backend.GoalStatus = state;
        var result = await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        Assert.Equal(ConversationResumeStatus.NoActionNeeded, result.Status);
        Assert.DoesNotContain(fixture.Backend.Calls, x => x.Method == "thread/goal/set");
    }

    [Fact]
    public async Task RepeatedResume_StartsOnlyOneGoal()
    {
        await using var fixture = new Fixture();
        await Task.WhenAll(fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None), fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None));
        Assert.Single(fixture.Backend.Calls, x => x.Method == "thread/goal/set");
        Assert.True(await fixture.Service.PauseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ActivationWithoutExecutionEvent_RemainsUnconfirmedAndIsNotResubmitted()
    {
        await using var fixture = new Fixture();
        await fixture.Service.DisposeAsync();
        fixture.Service = fixture.NewService(TimeSpan.FromMilliseconds(60));
        fixture.Backend.StartEvents = false;
        var result = await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        Assert.Equal(ConversationResumeStatus.PendingConfirmation, result.Status);
        Assert.True(fixture.Service.HasActiveWork);
        await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        Assert.Single(fixture.Backend.Calls, x => x.Method == "thread/goal/set");
        Assert.True(await fixture.Service.PauseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExitAdmissionGate_DoesNotStartAnotherGoal()
    {
        await using var fixture = new Fixture(); fixture.Service.PrepareToExit(true);
        var result = await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        Assert.Equal(ConversationResumeStatus.NoActionNeeded, result.Status);
        Assert.Empty(fixture.Backend.Calls);
    }

    [Fact]
    public async Task ExistingActiveTurn_IsNotTakenOver()
    {
        await using var fixture = new Fixture(); fixture.Backend.ThreadStatus = "active";
        var result = await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        Assert.Equal(ConversationResumeStatus.Busy, result.Status);
        Assert.DoesNotContain(fixture.Backend.Calls, x => x.Method == "thread/goal/set");
    }

    [Fact]
    public async Task InvalidRecoveryRecord_DoesNotStartAReplacementGoal()
    {
        await using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "settings"));
        File.WriteAllText(Path.Combine(fixture.Root, "settings", "goal-runtime.json"), "{broken");
        var result = await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        Assert.Equal(ConversationResumeStatus.PendingConfirmation, result.Status);
        Assert.Empty(fixture.Backend.Calls);
    }

    [Fact]
    public async Task WaitingApproval_IsExplicitAndScopesRepliesToLiveRequest()
    {
        await using var fixture = new Fixture(); await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        fixture.Backend.Latest!.Emit(new { id = "approval-1", method = "item/commandExecution/requestApproval", @params = new { threadId = Id, turnId = "turn-1", itemId = "command-1", command = "test command", cwd = fixture.Root } });
        await WaitAsync(() => fixture.Service.PendingRequests.Count == 1);
        Assert.Empty(fixture.Backend.Replies);
        var pending = fixture.Service.PendingRequests[0];
        fixture.Backend.Latest.Emit(new { id = "approval-1", method = "item/commandExecution/requestApproval", @params = new { threadId = Id, turnId = "turn-1", itemId = "command-1", command = "test command", cwd = fixture.Root } });
        await Task.Delay(50);
        Assert.Single(fixture.Service.PendingRequests);
        Assert.Equal(1, fixture.Notifications.Count);
        await fixture.Service.RespondAsync(pending.Key, "decline", null, CancellationToken.None);
        Assert.Equal("decline", Json(fixture.Backend.Replies[0].Result!).GetProperty("decision").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RespondAsync(pending.Key, "accept", null, CancellationToken.None));
        Assert.True(await fixture.Service.PauseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Restart_ReconcilesOwnedTurnWithoutSecondActivation()
    {
        await using var fixture = new Fixture(); await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        await fixture.Service.DisposeAsync();
        fixture.Service = fixture.NewService();
        await fixture.Service.InitializeAsync(CancellationToken.None);
        Assert.True(fixture.Service.HasActiveWork);
        Assert.Equal("turn-1", fixture.Service.State.TurnId);
        Assert.Single(fixture.Backend.Calls, x => x.Method == "thread/goal/set");
        Assert.True(await fixture.Service.PauseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnsupportedDynamicTool_PausesInsteadOfFabricatingSuccess()
    {
        await using var fixture = new Fixture(); await fixture.Service.ResumeAsync(fixture.Target, CancellationToken.None);
        fixture.Backend.Latest!.Emit(new { id = 11, method = "item/tool/call", @params = new { threadId = Id, turnId = "turn-1", tool = "desktopOnly" } });
        await WaitAsync(() => fixture.Backend.GoalStatus == "paused" && !fixture.Service.HasActiveWork);
        Assert.True(Assert.Single(fixture.Backend.Replies).Error);
    }

    [Fact]
    public void QuotaGate_AcceptsWeeklyOnlyButRequiresAuthoritativePermission()
    {
        var usage = new UsageData { Status = UsageStatus.Available, DataSource = "App Server", OrdinaryUsageAllowed = true, WeeklyRemainingPercent = 95 };
        Assert.True(GoalProtocol.UsageAllowsResume(usage));
        usage.OrdinaryUsageAllowed = null; Assert.False(GoalProtocol.UsageAllowsResume(usage));
        usage.OrdinaryUsageAllowed = true; usage.DataSource = "DOM"; Assert.False(GoalProtocol.UsageAllowsResume(usage));
        usage.DataSource = "App Server"; usage.WeeklyRemainingPercent = 0; Assert.False(GoalProtocol.UsageAllowsResume(usage));
    }

    [Fact]
    public void ApprovalWindow_RendersWithoutSendingAnyDecision()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var worker = new GoalResumeService(new Options(), _ => Task.CompletedTask, (_, _) => throw new InvalidOperationException(), new Notifications(), Path.Combine(Path.GetTempPath(), "unused-goal-journal.json"));
                var request = new GoalPendingRequest("preview", "item/commandExecution/requestApproval", Id, "turn-preview",
                    "git status\nWorking directory: C:\\SampleProject", Json(new { command = "git status" }), ["accept", "decline", "cancel"]);
                var window = new CodexUsageAssistant.Views.GoalApprovalWindow(worker, request);
                var content = (System.Windows.FrameworkElement)window.Content;
                content.Measure(new System.Windows.Size(760, 560)); content.Arrange(new System.Windows.Rect(0, 0, 760, 560)); content.UpdateLayout();
                Assert.Equal(3, ((System.Windows.Controls.StackPanel)window.FindName("DecisionsPanel")).Children.Count);
                Assert.Contains("git status", ((System.Windows.Controls.TextBox)window.FindName("DetailsText")).Text);
                var output = Environment.GetEnvironmentVariable("CODEX_UI_PREVIEW_ROOT");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(760, 560, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, "goal-approval.png")); encoder.Save(file);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10))); Assert.Null(failure);
    }

    [Fact]
    public void PermissionReply_CannotAddUnrequestedPermissions()
    {
        var parameters = Json(new { permissions = new { network = new { enabled = true } } });
        var request = new GoalPendingRequest("key", "item/permissions/requestApproval", Id, "turn", "", parameters, ["accept", "decline"]);
        var response = Json(GoalProtocol.Response(request, "accept", null));
        Assert.Equal("turn", response.GetProperty("scope").GetString());
        Assert.Equal(parameters.GetProperty("permissions").GetRawText(), response.GetProperty("permissions").GetRawText());
        Assert.Throws<InvalidOperationException>(() => GoalProtocol.Response(request, "acceptForSession", null));
    }

    [Fact]
    public async Task LiveNativeGoal_WhenEnabled_ResumesOnlyIsolatedTestThread()
    {
        if (Environment.GetEnvironmentVariable("CODEX_TEST_LIVE_BACKGROUND_GOAL") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "ezmate-live-goal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var portProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); portProbe.Start();
        var port = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        var options = await new JsonSettingsService().LoadAsync(CancellationToken.None) ?? new WindowPosition();
        options.HostPort = port;
        options.HostExecutablePath = AppServerUsageService.FindExecutable(options.CodexExecutablePath);
        options.HostWorkingDirectory = Path.Combine(root, "work");
        using var process = new AppServerHostRuntime().Start(options);
        IGoalRpcConnection? setup = null; GoalResumeService? worker = null; string? threadId = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        try
        {
            for (var i = 0; i < 20; i++)
            {
                try { setup = await GoalRpcConnection.ConnectAsync(port, timeout.Token); break; }
                catch (System.Net.WebSockets.WebSocketException) { await Task.Delay(250, timeout.Token); }
            }
            Assert.NotNull(setup);
            var created = await setup.RequestAsync("thread/start", new { cwd = options.HostWorkingDirectory, approvalPolicy = "on-request", sandbox = "read-only" }, timeout.Token);
            threadId = created.GetProperty("thread").GetProperty("id").GetString()!;
            await setup.RequestAsync("thread/goal/set", new
            {
                threadId,
                objective = "This is an isolated Codex EzMate background-resume integration test. Do not read or change files, run commands, use external tools, or access the network. The only objective is to answer EZMATE_BACKGROUND_OK and mark this test goal complete using the native update_goal tool.",
                status = "usageLimited"
            }, timeout.Token);
            var savedOptions = new LiveOptions(options);
            worker = new GoalResumeService(savedOptions, _ => Task.CompletedTask, GoalRpcConnection.ConnectAsync, new Notifications(), Path.Combine(root, "settings", "goal-runtime.json"));
            var result = await worker.ResumeAsync(new ConversationTarget { ThreadId = threadId, Title = "EzMate isolated background test", Enabled = true }, timeout.Token);
            output.WriteLine($"Native resume result={result.Status}; state={worker.State.Status}; message={result.Message}");
            Assert.Contains(result.Status, new[] { ConversationResumeStatus.Resumed, ConversationResumeStatus.Completed, ConversationResumeStatus.PendingConfirmation });
            while (worker.HasActiveWork && !timeout.IsCancellationRequested)
            {
                Assert.Empty(worker.PendingRequests);
                await Task.Delay(250, timeout.Token);
            }
            Assert.Equal(ConversationResumeStatus.Completed, worker.State.Status);
            output.WriteLine("PASS: native usageLimited -> active -> turn started -> complete on an isolated persisted thread; no foreground automation.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (worker is not null) { await worker.PauseAsync(cleanup.Token); await worker.DisposeAsync(); }
            if (setup is not null)
            {
                if (threadId is not null)
                {
                    try
                    {
                        var finalGoal = await setup.RequestAsync("thread/goal/get", new { threadId }, cleanup.Token);
                        if (GoalProtocol.Text(GoalProtocol.Property(finalGoal, "goal"), "status") != "complete")
                            await setup.RequestAsync("thread/goal/set", new { threadId, status = "paused" }, cleanup.Token);
                    }
                    catch (HostRpcException) { }
                    try { await setup.RequestAsync("thread/archive", new { threadId }, cleanup.Token); }
                    catch (HostRpcException) { }
                }
                await setup.DisposeAsync();
            }
            process.Stop();
            Directory.Delete(root, true);
        }
    }

    private sealed class LiveOptions(WindowPosition options) : ISettingsService
    {
        public Task<WindowPosition?> LoadAsync(CancellationToken token) => Task.FromResult<WindowPosition?>(options);
        public Task SaveAsync(WindowPosition value, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class MonitorSettings : IAutoResumeSettingsService
    {
        public AutoResumeSettings Value = new();
        public Task<AutoResumeSettings> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(AutoResumeSettings value, CancellationToken token) { Value = value; return Task.CompletedTask; }
    }
    private sealed class Cache : IUsageCacheService
    {
        public Task<UsageData?> LoadAsync(CancellationToken token) => Task.FromResult<UsageData?>(null);
        public Task SaveAsync(UsageData value, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class ScheduledGoals : IGoalResumeService
    {
        public UsageData Usage = new() { Status = UsageStatus.Available, DataSource = "App Server", OrdinaryUsageAllowed = true, WeeklyRemainingPercent = 95 };
        public List<string> Resumed = [];
        public bool HasActiveWork { get; set; }
        public GoalExecutionState State => new(null, null, ConversationResumeStatus.Unknown, "");
        public IReadOnlyList<GoalPendingRequest> PendingRequests => [];
        public event Action? Changed { add { } remove { } }
        public event Action? UsageChanged { add { } remove { } }
        public Task<UsageData> ReadUsageAsync(CancellationToken token) => Task.FromResult(Usage);
        public Task<ConversationResumeResult> ResumeAsync(ConversationTarget target, CancellationToken token)
        {
            Resumed.Add(target.ThreadId!); HasActiveWork = true;
            return Task.FromResult(new ConversationResumeResult(target.ProjectName, target.Title, ConversationResumeStatus.Resumed, "test", DateTimeOffset.Now));
        }
        public Task<ConversationResumeResult> ContinuePausedAsync(ConversationTarget target, CancellationToken token) => ResumeAsync(target, token);
        public Task<bool> PauseAsync(CancellationToken token) { HasActiveWork = false; return Task.FromResult(true); }
        public Task RespondAsync(string key, string decision, IReadOnlyDictionary<string, string[]>? answers, CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task Scheduler_WeeklyOnlyRecoveryDoesNotNeedPreviousExhaustionObservation()
    {
        var settings = new MonitorSettings { Value = new() { AwaitingReset = false, Conversations = [
            new() { ThreadId = Id, Enabled = true }, new() { ThreadId = "01900000-0000-7000-8000-000000000002", Enabled = true }] } };
        var goals = new ScheduledGoals();
        using var scheduler = new AutoResumeScheduler(null!, new Cache(), settings, goals, new Notifications());
        await scheduler.RunNowAsync(CancellationToken.None);
        Assert.Equal(new[] { Id }, goals.Resumed);
        await scheduler.RunNowAsync(CancellationToken.None);
        Assert.Single(goals.Resumed);
    }

    [Fact]
    public async Task Scheduler_EmptyAllowlistDoesNotTargetForegroundConversation()
    {
        var goals = new ScheduledGoals();
        using var scheduler = new AutoResumeScheduler(null!, new Cache(), new MonitorSettings(), goals, new Notifications());
        await scheduler.RunNowAsync(CancellationToken.None);
        Assert.Empty(goals.Resumed);
    }

    [Fact]
    public async Task Scheduler_StopsNewWorkButDoesNotInterruptCurrentGoal()
    {
        var goals = new ScheduledGoals { HasActiveWork = true };
        var settings = new MonitorSettings();
        using var scheduler = new AutoResumeScheduler(null!, new Cache(), settings, goals, new Notifications());
        await scheduler.StopAsync(CancellationToken.None);
        Assert.True(goals.HasActiveWork);
        Assert.False(settings.Value.MonitorEnabled);
    }
}

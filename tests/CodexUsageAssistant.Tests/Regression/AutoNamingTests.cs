using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Services;
using Xunit;

namespace CodexUsageAssistant.Tests.Regression;

public sealed class AutoNamingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EzMateNaming-" + Guid.NewGuid().ToString("N"));
    private const string Id = "01900000-0000-7000-8000-000000000099";
    private string Rollout => Path.Combine(_root, "sessions", "rollout-" + Id + ".jsonl");
    private SessionBrowserStore Store => new(_root, _root);
    private void Append(params object[] records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Rollout)!);
        File.AppendAllText(Rollout, string.Concat(records.Select(r => JsonSerializer.Serialize(r) + "\n")), new System.Text.UTF8Encoding(false));
    }
    private static object Meta(object? source = null, string? cwd = null) => new { type = "session_meta", payload = new { id = Id, source = source ?? "vscode", cwd } };
    private static object Event(string type, string turn = "t1", string message = "") => new { type = "event_msg", payload = new { type, turn_id = turn, message } };
    private static object User(string text) => new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text } } } };
    private void Turn(string id, string text) => Append(Event("task_started", id), Event("user_message", id, text), User(text), Event("task_complete", id));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData("  Hello\r\n  world  ", "Hello…")]
    [InlineData("<environment_context>injected</environment_context>", "")]
    [InlineData("[Codex EzMate test-id]\nContinue", "")]
    [InlineData("# AGENTS.md instructions\nprivate instructions", "")]
    [InlineData("The following is the Codex agent history whose request action you are assessing.", "")]
    [InlineData("<image name=\"file\">C:/private.png</image>", "")]
    [InlineData("# Files mentioned by the user:\nprivate.png\n## My request:\nPlease fix this", "Pleas…")]
    public void FormattingFiltersInjectedContent(string input, string expected) => Assert.Equal(expected, SessionNameFormatter.UserText(input));

    [Fact]
    public void FormattingCountsGraphemesWithoutSplittingEmoji()
    {
        var five = string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 5));
        Assert.Equal(five, SessionNameFormatter.Shorten(five));
        Assert.Equal(five + "…", SessionNameFormatter.Shorten(five + "中"));
    }

    [Fact]
    public void ProjectUsesProjectNameAndLatestPrompt()
    {
        Append(Meta(cwd: @"S:\workspace\codexHelper"));
        Turn("t1", "First prompt");
        Turn("t2", "Latest prompt");
        Assert.Equal("codexHelper | Lates…", new SessionAutoNameObserver().Read(Rollout, 0, default)!.Name);
    }

    [Fact]
    public void ObserverDeduplicatesResponseAndEventAndRequiresCompletion()
    {
        Append(Meta(), Event("task_started"), Event("user_message", message: "First"), User("First"), Event("user_message", message: "First"));
        var observer = new SessionAutoNameObserver();
        Assert.Null(observer.Read(Rollout, 0, default));
        Append(Event("task_complete"));
        Assert.Equal("First", observer.Read(Rollout, 0, default)!.Name);
        Turn("t2", "Latest");
        Assert.Equal("First | Lates…", observer.Read(Rollout, 0, default)!.Name);
        Append(Event("task_complete", "t2"));
        Assert.Null(observer.Read(Rollout, 0, default));
    }

    [Fact]
    public void ObserverSupportsResponseOnlyAndRejectsInternalSessions()
    {
        Append(Meta(), User("# AGENTS.md instructions\nignore"), Event("task_started"), User("Actual prompt"), Event("task_complete"));
        Assert.Equal("Actua…", new SessionAutoNameObserver().Read(Rollout, 0, default)!.Name);
        File.WriteAllText(Rollout, "");
        Append(Meta(new { subagent = "review" })); Turn("t2", "Internal prompt");
        Assert.Null(new SessionAutoNameObserver().Read(Rollout, 0, default));
    }

    [Fact]
    public void MultipleResponseOnlyPromptsRetainFirstAndLatest()
    {
        Append(Meta(), Event("task_started"), User("First"), User("Latest"), Event("task_complete"));
        Assert.Equal("First | Lates…", new SessionAutoNameObserver().Read(Rollout, 0, default)!.Name);
    }

    [Theory]
    [InlineData("task_failed")]
    [InlineData("turn_aborted")]
    [InlineData("task_aborted")]
    [InlineData("error")]
    public void FailedTurnsDoNotRename(string failure)
    {
        Append(Meta(), Event("task_started"), Event("user_message", message: "Actual"), Event(failure), Event("task_complete"));
        Assert.Null(new SessionAutoNameObserver().Read(Rollout, 0, default));
    }

    [Fact]
    public void PartialUtf8AndTruncationDoNotReplayOldCompletions()
    {
        Append(Meta());
        var observer = new SessionAutoNameObserver(); observer.Read(Rollout, 0, default);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Event("user_message", message: "中文")) + "\n");
        Append(Event("task_started"));
        using (var stream = File.Open(Rollout, FileMode.Append)) stream.Write(bytes, 0, bytes.Length - 4);
        Assert.Null(observer.Read(Rollout, 0, default));
        using (var stream = File.Open(Rollout, FileMode.Append)) stream.Write(bytes, bytes.Length - 4, 4);
        Append(Event("task_complete"));
        Assert.Equal("中文", observer.Read(Rollout, 0, default)!.Name);
        File.WriteAllText(Rollout, ""); Append(Meta()); Turn("new", "New");
        Assert.Null(observer.Read(Rollout, 0, default));
        Turn("next", "Next"); Assert.Equal("New | Next", observer.Read(Rollout, observer.Offset, default)!.Name);
    }

    [Fact]
    public async Task ExclusionCancelsQueuedWorkAndRestoreWaitsForNewCompletion()
    {
        Append(Meta()); Turn("old", "First");
        var writer = new FakeWriter();
        using var service = new SessionAutoNamingService(Store, writer);
        await service.SetEnabledAsync(true);
        await service.TickAsync(default); Assert.Empty(writer.Writes);
        Turn("second", "Second");
        var pending = Assert.Single(await service.ObserveAsync(default));
        await service.SetExcludedAsync([Id], true);
        await service.ApplyAsync(pending, default); Assert.Empty(writer.Writes);
        Turn("excluded", "Excluded message");
        await service.SetExcludedAsync([Id], false);
        await service.TickAsync(default); Assert.Empty(writer.Writes);
        Turn("after", "New reply"); await service.TickAsync(default);
        Assert.Equal("First | New r…", Assert.Single(writer.Writes));
        await service.SetExcludedAsync([Id], true);
        writer.Name = "Manually renamed";
        using var restarted = new SessionAutoNamingService(Store, writer);
        await restarted.TickAsync(default);
        Assert.True(restarted.Enabled); Assert.True(restarted.IsExcluded(Id));
        Turn("excluded2", "No rename"); await restarted.TickAsync(default);
        Assert.Single(writer.Writes); Assert.Equal("Manually renamed", writer.Name);
        var settings = File.ReadAllText(Path.Combine(_root, "settings", "session-auto-naming.json"));
        Assert.DoesNotContain("First", settings); Assert.Contains(Id, settings);
    }

    [Fact]
    public async Task RestartAndDisableDoNotReplayPendingWork()
    {
        Append(Meta()); var writer = new FakeWriter();
        using (var service = new SessionAutoNamingService(Store, writer))
        {
            await service.SetEnabledAsync(true); Turn("t1", "Prompt");
            var pending = Assert.Single(await service.ObserveAsync(default));
            await service.SetEnabledAsync(false); await service.ApplyAsync(pending, default);
            Assert.Empty(writer.Writes); await service.SetEnabledAsync(true);
        }
        using var restarted = new SessionAutoNamingService(Store, writer);
        await restarted.TickAsync(default); Assert.Empty(writer.Writes);
        Turn("t2", "Next"); await restarted.TickAsync(default); Assert.Single(writer.Writes);
    }

    [Fact]
    public async Task ExclusionWaitsForSentMutationAndTimeoutIsNotRetried()
    {
        Append(Meta()); var writer = new FakeWriter { Hold = true, TimeoutAfterWrite = true };
        using var service = new SessionAutoNamingService(Store, writer);
        await service.SetEnabledAsync(true); Turn("t1", "Prompt");
        var pending = Assert.Single(await service.ObserveAsync(default));
        var apply = service.ApplyAsync(pending, default);
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var exclude = service.SetExcludedAsync([Id], true);
        Assert.False(exclude.IsCompleted);
        var confirmed = false;
        service.NameUpdated += (_, _) => confirmed = true;
        writer.Release.TrySetResult(); await apply; await exclude;
        Assert.Single(writer.Writes); Assert.True(confirmed);
        Assert.True(service.IsExcluded(Id));
    }

    [Fact]
    public async Task UnknownMutationSurvivesRestartAndIsNeverReportedAsConfirmedExclusion()
    {
        Append(Meta()); var writer = new FakeWriter { UnknownOutcome = true };
        using (var service = new SessionAutoNamingService(Store, writer))
        {
            await service.SetEnabledAsync(true); Turn("t1", "Original prompt"); await service.TickAsync(default);
            Assert.Equal("uncertain", service.Status);
            await service.SetExcludedAsync([Id], true); Assert.Equal("uncertain", service.Status);
            await service.SetExcludedAsync([Id], false);
        }
        using var restarted = new SessionAutoNamingService(Store, writer);
        await restarted.TickAsync(default); Assert.Equal("uncertain", restarted.Status);
        Turn("t2", "Do not resend"); await restarted.TickAsync(default);
        Assert.Single(writer.Writes);
        Assert.DoesNotContain("Original prompt", File.ReadAllText(Path.Combine(_root, "settings", "session-auto-naming.json")));
    }

    [Theory]
    [InlineData(CodexUsageAssistant.Models.AppLanguage.English, "Automatically name sessions")]
    [InlineData(CodexUsageAssistant.Models.AppLanguage.TraditionalChinese, "自動命名會話")]
    [InlineData(CodexUsageAssistant.Models.AppLanguage.SimplifiedChinese, "自动命名会话")]
    public async Task BrowserClosesButBackgroundNamingContinues(CodexUsageAssistant.Models.AppLanguage language, string label)
    {
        Append(Meta()); var writer = new FakeWriter();
        using var service = new SessionAutoNamingService(Store, writer);
        await service.SetEnabledAsync(true);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new CodexUsageAssistant.Views.SessionBrowserWindow(Store, service);
                window.SetLanguage(language);
                var panel = (System.Windows.Controls.DockPanel)window.Content;
                var checks = Descendants(panel).OfType<System.Windows.Controls.CheckBox>();
                Assert.DoesNotContain(checks, c => Equals(c.Content, label));
                window.Close();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        Assert.Null(failure);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NameUpdated += (_, _) => updated.TrySetResult();
        service.Start(); Turn("next", "Background prompt");
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("Backg…", Assert.Single(writer.Writes));
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent)
    {
        foreach (var item in System.Windows.LogicalTreeHelper.GetChildren(parent).OfType<System.Windows.DependencyObject>())
        {
            yield return item;
            foreach (var child in Descendants(item)) yield return child;
        }
    }

    private sealed class FakeWriter : ISessionNameWriter
    {
        internal readonly List<string> Writes = [];
        internal string? Name;
        internal bool Hold, TimeoutAfterWrite, UnknownOutcome;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> ReadAsync(string id, CancellationToken token) => Task.FromResult(Name);
        public async Task WriteAsync(string id, string name, CancellationToken token)
        {
            Writes.Add(name); Entered.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(token);
            if (UnknownOutcome) throw new OperationCanceledException();
            Name = name;
            if (TimeoutAfterWrite) throw new OperationCanceledException();
        }
    }
    [Fact]
    public async Task LiveNameRoundTrip_WithoutGeneratingAnyTurn()
    {
        if (Environment.GetEnvironmentVariable("CODEX_TEST_NAMING") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var session = new UsageAppServerSession(new JsonSettingsService());
        var started = await session.RequestAsync(null, "thread/start", new { ephemeral = false, approvalPolicy = "never", sandbox = "read-only" }, timeout.Token);
        var id = started.GetProperty("thread").GetProperty("id").GetString()!;
        try
        {
            const string title = "EzMate naming verification | No AI request";
            await session.RequestAsync(null, "thread/name/set", new { threadId = id, name = title }, timeout.Token);
            var read = await session.RequestAsync(null, "thread/read", new { threadId = id, includeTurns = false }, timeout.Token);
            Assert.Equal(title, read.GetProperty("thread").GetProperty("name").GetString());
            var names = new SessionBrowserStore().LoadNames(timeout.Token);
            Assert.Equal(title, names[id].Name);
        }
        finally
        {
            // Archive only the empty test thread created above.
            try { await session.RequestAsync(null, "thread/archive", new { threadId = id }, timeout.Token); }
            catch (InvalidOperationException) { /* Empty threads may not have a persisted rollout to archive. */ }
        }
    }
}

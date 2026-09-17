using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Xunit;
using Xunit.Abstractions;

namespace CodexUsageAssistant.Tests;

public sealed class AppServerHostTests(ITestOutputHelper output)
{
    [Fact]
    public void HostDefaults_AutofillWithoutOverwritingManualPath()
    {
        var options = new WindowPosition { CodexExecutablePath = "invalid-usage-path" };
        Assert.True(AppServerHostRuntime.FillDefaults(options, () => @"C:\Codex\codex.exe"));
        Assert.Equal(@"C:\Codex\codex.exe", options.HostExecutablePath);
        Assert.NotNull(options.HostWorkingDirectory);
        Assert.True(Path.IsPathFullyQualified(options.HostWorkingDirectory));
        Assert.False(AppServerHostRuntime.FillDefaults(options, () => throw new Exception("Must preserve explicit path")));
    }
    private sealed class MemorySettings : ISettingsService
    {
        public WindowPosition Value = new();
        public Task<WindowPosition?> LoadAsync(CancellationToken token) => Task.FromResult<WindowPosition?>(Value);
        public Task SaveAsync(WindowPosition value, CancellationToken token) { Value = value; return Task.CompletedTask; }
    }
    private sealed class FakeProcess : IHostProcess
    {
        public bool HasExited { get; set; }
        public int Id => 123;
        public bool Stopped;
        public void Stop() { HasExited = true; Stopped = true; }
        public void Dispose() { }
    }
    private sealed class FakeRuntime : IAppServerHostRuntime
    {
        public HostProbe Initial = HostProbe.Missing;
        public HostProbe Running = HostProbe.Ready;
        public List<FakeProcess> Processes = [];
        public Task<HostProbe> ProbeAsync(int port, CancellationToken token) => Task.FromResult(Processes.LastOrDefault() is { HasExited: false } ? Running : Initial);
        public IHostProcess Start(WindowPosition settings) { var process = new FakeProcess(); Processes.Add(process); return process; }
    }
    private static async Task WaitAsync(Func<bool> predicate, int seconds = 8)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate() && timeout.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(50);
        Assert.True(predicate(), "Timed out waiting for host state.");
    }
    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "codex-host-tests-" + Guid.NewGuid());

    [Theory]
    [InlineData(HostProbe.Ready, AppServerHostState.Ready)]
    [InlineData(HostProbe.LoginRequired, AppServerHostState.LoginRequired)]
    [InlineData(HostProbe.Incompatible, AppServerHostState.ConnectionError)]
    public async Task ExistingEndpoint_IsNeverKilledOrDuplicated(object probe, AppServerHostState state)
    {
        var root = TempRoot();
        var runtime = new FakeRuntime { Initial = (HostProbe)probe };
        try
        {
            await using var host = new CodexAppServerHost(new MemorySettings(), runtime, root, Path.Combine(root, "logs/host.log"));
            await host.ApplySettingsAsync(new WindowPosition());
            await WaitAsync(() => host.State == state);
            Assert.False(host.OwnsServer);
            await host.RestartAsync();
            await host.StopAsync();
            Assert.Empty(runtime.Processes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OwnedServer_CrashRestartsAndStopCleansOnlyOwnedProcess()
    {
        var root = TempRoot(); var runtime = new FakeRuntime();
        try
        {
            await using var host = new CodexAppServerHost(new MemorySettings(), runtime, root, Path.Combine(root, "logs/host.log"));
            await host.ApplySettingsAsync(new WindowPosition());
            await WaitAsync(() => host.State == AppServerHostState.Ready);
            Assert.True(host.OwnsServer);
            runtime.Processes[0].HasExited = true;
            var elapsed = Stopwatch.StartNew();
            await WaitAsync(() => runtime.Processes.Count == 2 && host.State == AppServerHostState.Ready);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(2));
            await host.StopAsync();
            Assert.True(runtime.Processes.All(x => x.Stopped));
            Assert.Equal(AppServerHostState.Stopped, host.State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LoginRequired_DoesNotRestartOwnedServer()
    {
        var root = TempRoot(); var runtime = new FakeRuntime { Running = HostProbe.LoginRequired };
        try
        {
            await using var host = new CodexAppServerHost(new MemorySettings(), runtime, root, Path.Combine(root, "logs/host.log"));
            await host.ApplySettingsAsync(new WindowPosition());
            await WaitAsync(() => host.State == AppServerHostState.LoginRequired);
            host.CheckNow(); await Task.Delay(500);
            Assert.Single(runtime.Processes);
            Assert.False(runtime.Processes[0].Stopped);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentHosts_ShareStartupLockAndReuseOneServer()
    {
        var root = TempRoot(); var runtime = new FakeRuntime();
        try
        {
            await using var first = new CodexAppServerHost(new MemorySettings(), runtime, root, Path.Combine(root, "logs/one.log"));
            await using var second = new CodexAppServerHost(new MemorySettings(), runtime, root, Path.Combine(root, "logs/two.log"));
            await Task.WhenAll(first.ApplySettingsAsync(new WindowPosition()), second.ApplySettingsAsync(new WindowPosition()));
            await WaitAsync(() => first.State == AppServerHostState.Ready && second.State == AppServerHostState.Ready);
            Assert.Single(runtime.Processes);
            Assert.NotEqual(first.OwnsServer, second.OwnsServer);
            var external = first.OwnsServer ? second : first;
            await external.StopAsync();
            Assert.False(runtime.Processes[0].Stopped);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Backoff_IsBoundedAndSettingsRoundTrip()
    {
        Assert.Equal(new[] { 2, 5, 15, 30, 60, 60, 60 }, Enumerable.Range(0, 7).Select(CodexAppServerHost.RetrySeconds));
        var value = new WindowPosition { HostPort = 4500, HostAutoStart = false, HostExecutablePath = @"C:\Codex\codex.exe", HostWorkingDirectory = @"C:\Empty" };
        var loaded = JsonSerializer.Deserialize<WindowPosition>(JsonSerializer.Serialize(value))!;
        Assert.False(loaded.HostAutoStart); Assert.Equal(value.HostExecutablePath, loaded.HostExecutablePath);
        Assert.Equal(value.HostWorkingDirectory, loaded.HostWorkingDirectory); Assert.Equal(4500, loaded.HostPort);
    }

    [Theory]
    [InlineData("{\"account\":null,\"requiresOpenaiAuth\":true}", HostProbe.LoginRequired)]
    [InlineData("{\"account\":{\"type\":\"chatgpt\"}}", HostProbe.Ready)]
    [InlineData("{\"account\":{\"type\":\"apiKey\"},\"requiresOpenaiAuth\":false}", HostProbe.LoginRequired)]
    public void Health_RequiresAppropriateAccount(string json, object expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal((HostProbe)expected, AppServerHostRuntime.ClassifyAccount(document.RootElement));
    }

    [Fact]
    public void WorkingDirectory_RejectsInheritedProjectInstructions()
    {
        var root = TempRoot(); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "Test instructions", System.Text.Encoding.UTF8);
            Assert.Throws<InvalidDataException>(() => AppServerHostRuntime.Validate(new WindowPosition
            { HostExecutablePath = Environment.ProcessPath, HostWorkingDirectory = Path.Combine(root, "empty") }, false));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InvalidConfiguration_IsHandledWithoutFaultingLifecycle()
    {
        Assert.True(CodexAppServerHost.IsExpected(new InvalidDataException("Invalid work directory")));
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "CodexUsageAssistant", "empty-validation-" + Guid.NewGuid());
        AppServerHostRuntime.Validate(new WindowPosition { HostExecutablePath = Environment.ProcessPath, HostWorkingDirectory = path }, false);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task LiveHost_WhenEnabled_OwnedAndExternalLifecycle()
    {
        if (Environment.GetEnvironmentVariable("CODEX_TEST_LIVE_HOST") != "1") return;
        var root = TempRoot(); Directory.CreateDirectory(root);
        var portLease = new TcpListener(IPAddress.Loopback, 0); portLease.Start();
        var port = ((IPEndPoint)portLease.LocalEndpoint).Port; portLease.Stop();
        var options = await new JsonSettingsService().LoadAsync(CancellationToken.None) ?? new WindowPosition();
        options.HostExecutablePath = AppServerUsageService.FindExecutable(options.CodexExecutablePath);
        options.HostWorkingDirectory = Path.Combine(root, "empty"); options.HostPort = port; options.HostAutoStart = true;
        var settings = new MemorySettings { Value = options };
        try
        {
            await using var owner = new CodexAppServerHost(settings, new AppServerHostRuntime(), root, Path.Combine(root, "logs/owner.log"));
            await owner.ApplySettingsAsync(options);
            await WaitAsync(() => owner.State == AppServerHostState.Ready, 40);
            Assert.True(owner.OwnsServer);
            var models = await owner.ListModelsAsync(); Assert.NotEmpty(models);
            await using var guest = new CodexAppServerHost(settings, new AppServerHostRuntime(), root, Path.Combine(root, "logs/guest.log"));
            await guest.ApplySettingsAsync(options);
            await WaitAsync(() => guest.State == AppServerHostState.Ready);
            Assert.False(guest.OwnsServer);
            await guest.RestartAsync(); await guest.StopAsync();
            Assert.Equal(HostProbe.Ready, await new AppServerHostRuntime().ProbeAsync(port, CancellationToken.None));
            await owner.StopAsync();
            await WaitAsync(() => !owner.IsRunning);
            Assert.Equal(HostProbe.Missing, await new AppServerHostRuntime().ProbeAsync(port, CancellationToken.None));
            output.WriteLine($"Owned start, model list ({models.Count}), external reuse and owned-only shutdown passed on loopback port {port}; no generation requests.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ForeignTcpService_IsReportedWithoutStoppingListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    try { await client.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellation.Token); }
                    catch (IOException) { }
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var result = await new AppServerHostRuntime().ProbeAsync(port, CancellationToken.None);
            Assert.Equal(HostProbe.Incompatible, result);
            using var stillAlive = new TcpClient();
            await stillAlive.ConnectAsync(IPAddress.Loopback, port);
            Assert.True(stillAlive.Connected);
        }
        finally { cancellation.Cancel(); await server; listener.Stop(); }
    }
}

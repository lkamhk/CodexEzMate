using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.Views;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class SessionBrowserTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void LiveReadOnlyScan_WhenExplicitlyEnabled()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_TEST_BROWSER_HOME");
        if (string.IsNullOrWhiteSpace(home)) return;
        Assert.True(Directory.Exists(Path.Combine(home, "sessions")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var scan = new SessionBrowserStore(home).Scan(timeout.Token);
        Assert.NotEmpty(scan.Sessions);
        output.WriteLine($"Read-only scan: {scan.Sessions.Count} sessions; {scan.Skipped} unreadable paths; no file operations performed.");
    }

    [Fact]
    public void SimplifiedLabels_DoNotReadBeyondNativeOutputBuffer()
    {
        for (var i = 0; i < 500; i++)
        {
            Assert.Equal("更新时间", LocalizationService.ToSimplified("更新時間"));
            Assert.Equal("备份", LocalizationService.ToSimplified("備份"));
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ezmate-browser-" + Guid.NewGuid());
        public string Home => Path.Combine(Root, "codex");
        public SessionBrowserStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Home); Store = new(Home, Root); }
        public string Write(string relative, string text)
        {
            var path = Path.GetFullPath(Path.Combine(Home, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false)); return path;
        }
        public BrowserSession Session(bool archived = false, object? source = null, string id = "ab123456-1234-1234-1234-123456789abc")
        {
            var records = new object[] {
                new { type = "session_meta", payload = new { meta = new { id, cwd = "C:/Example project", model_provider = "openai", title = "Embedded title", source = source ?? "cli" } } },
                new { type = "event_msg", payload = new { type = "user_message", message = "Hello world" } },
                new { type = "response_item", timestamp = "2026-09-17", payload = new { type = "message", role = "user", content = new[] {new {text = "Hello world"}} } },
                new { type = "response_item", payload = new { type = "message", role = "assistant", content = "Answer" } }
            };
            var path = Write((archived ? "archived_sessions" : "sessions") + $"/2026/09/17/rollout-{id}.jsonl", string.Join("\n", records.Select(r => JsonSerializer.Serialize(r))) + "\n{unfinished");
            return Store.Scan(default).Sessions.Single(s => s.Path == path);
        }
        public void Dispose() { Directory.Delete(Root, true); }
    }

    [Fact]
    public void ScanAndPreview_ParseNestedMetadataAndIgnorePartialJsonAndDuplicateUserEvents()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        Assert.Equal("Embedded title", session.Name); Assert.Equal("Hello world", session.Preview);
        Assert.Equal("C:/Example project", session.Project); Assert.False(session.Internal);
        var preview = fixture.Store.ReadPreview(session, default);
        Assert.Equal(2, preview.Messages.Count); Assert.Equal("user", preview.Messages[0].Role); Assert.Equal("Answer", preview.Messages[1].Text);
        Assert.False(preview.Truncated);
    }

    [Theory]
    [InlineData("sub_agent", true)]
    [InlineData("guardian", true)]
    [InlineData("vscode", false)]
    [InlineData("cli", false)]
    public void InternalFilter_UsesMetadataRatherThanMessageOrTitle(string source, bool expected)
    {
        using var fixture = new Fixture(); var session = fixture.Session(source: source);
        session.Name = "The following is the Codex agent history whose request action you are assessing.";
        Assert.Equal(expected, session.Internal);
        Assert.Equal(!expected, SessionBrowserSettings.Matches(session, "", "All sessions", true));
    }

    [Fact]
    public void InternalObjectMetadata_IsRecognized()
    {
        using var fixture = new Fixture(); Assert.True(fixture.Session(source: new { subagent = new { thread_spawn = "parent" } }).Internal);
    }

    [Fact]
    public void LatestIndexNameOverridesSqlite_ExplicitClearRemovesStaleName()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Home, "state_5.sqlite"), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE threads(id TEXT, title TEXT); INSERT INTO threads VALUES($id,'Database title')";
            command.Parameters.AddWithValue("$id", session.Id); command.ExecuteNonQuery();
        }
        var databaseBefore = File.ReadAllBytes(Path.Combine(fixture.Home, "state_5.sqlite"));
        Assert.Equal("Database title", fixture.Store.Scan(default).Sessions.Single().Name);
        fixture.Write("session_index.jsonl", JsonSerializer.Serialize(new { id = session.Id, thread_name = "Old name" }) + "\n" + JsonSerializer.Serialize(new { payload = new { thread_id = session.Id, thread_name = "New name" } }));
        Assert.Equal("New name", fixture.Store.Scan(default).Sessions.Single().Name);
        File.AppendAllText(Path.Combine(fixture.Home, "session_index.jsonl"), "\n" + JsonSerializer.Serialize(new { id = session.Id, thread_name = "" }));
        Assert.Equal("Embedded title", fixture.Store.Scan(default).Sessions.Single().Name);
        Assert.Equal(databaseBefore, File.ReadAllBytes(Path.Combine(fixture.Home, "state_5.sqlite")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupTrashAndRestore_RoundTripBytesAndLegacyManifest(bool archived)
    {
        using var fixture = new Fixture(); var session = fixture.Session(archived); var bytes = File.ReadAllBytes(session.Path);
        var backupPath = fixture.Store.Apply(session, BrowserFileAction.Backup, default);
        Assert.Equal(bytes, File.ReadAllBytes(session.Path)); Assert.Equal(bytes, File.ReadAllBytes(backupPath));
        using (var manifest = JsonDocument.Parse(File.ReadAllText(backupPath + ".bkup.json")))
        {
            Assert.Equal(1, manifest.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(archived ? "archived" : "active", manifest.RootElement.GetProperty("original_kind").GetString());
            Assert.StartsWith("2026/09/17/", manifest.RootElement.GetProperty("relative_path").GetString());
        }
        var second = fixture.Store.Apply(session, BrowserFileAction.Backup, default); Assert.NotEqual(backupPath, second);
        var trashPath = fixture.Store.Apply(session, BrowserFileAction.Trash, default); Assert.False(File.Exists(session.Path));
        var scan = fixture.Store.Scan(default).Sessions;
        Assert.Equal(session.Path, fixture.Store.Apply(scan.Single(s => s.Deleted), BrowserFileAction.Restore, default));
        Assert.Equal(bytes, File.ReadAllBytes(session.Path)); Assert.False(File.Exists(trashPath));
        var backup = scan.Single(s => s.Path == backupPath);
        Assert.Throws<IOException>(() => fixture.Store.Apply(backup, BrowserFileAction.Restore, default));
        File.Delete(session.Path);
        fixture.Store.Apply(backup, BrowserFileAction.Restore, default);
        Assert.Equal(bytes, File.ReadAllBytes(session.Path)); Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public void LegacyPythonBackup_RestoresPortableRelativePathWithoutTrustingOldAbsolutePath()
    {
        using var fixture = new Fixture(); var session = fixture.Session(); var backupPath = fixture.Store.Apply(session, BrowserFileAction.Backup, default);
        File.WriteAllText(backupPath + ".bkup.json", JsonSerializer.Serialize(new { version = 1, session_id = session.Id, original_kind = "active", relative_path = "2026/09/17/" + Path.GetFileName(session.Path), original_path = "X:/OtherMachine/sessions/wrong.jsonl" }), new UTF8Encoding(true));
        File.Delete(session.Path);
        var backup = fixture.Store.Scan(default).Sessions.Single(s => s.Backup);
        Assert.Equal(session.Path, fixture.Store.Apply(backup, BrowserFileAction.Restore, default));
    }

    [Theory]
    [InlineData("../outside.jsonl")]
    [InlineData("C:/outside.jsonl")]
    [InlineData("file.jsonl:stream")]
    public void RestoreRejectsMaliciousMetadata(string relative)
    {
        using var fixture = new Fixture(); var session = fixture.Session(); var trash = fixture.Store.Apply(session, BrowserFileAction.Trash, default);
        File.WriteAllText(trash + ".trash.json", JsonSerializer.Serialize(new { original_kind = "active", relative_path = relative }));
        var deleted = fixture.Store.Scan(default).Sessions.Single();
        Assert.Throws<IOException>(() => fixture.Store.Apply(deleted, BrowserFileAction.Restore, default));
        Assert.True(File.Exists(trash)); Assert.False(File.Exists(session.Path));
    }

    [Fact]
    public void ForeignSourceAndConcurrentWriter_AreRejectedWithoutLosingData()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        var fake = new BrowserSession { Path = fixture.Write("foreign.jsonl", "private"), Id = session.Id };
        Assert.Throws<IOException>(() => fixture.Store.Apply(fake, BrowserFileAction.Trash, default));
        using var writer = new FileStream(session.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.Throws<IOException>(() => fixture.Store.Apply(session, BrowserFileAction.Trash, default));
        Assert.True(File.Exists(session.Path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Home, "session_browser_trash"), "*.trash.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void CancelledOperationAndOtherBrowserLock_DoNotMutateSession()
    {
        using var fixture = new Fixture(); var session = fixture.Session(); using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Store.Apply(session, BrowserFileAction.Backup, cts.Token));
        var trashRoot = Path.Combine(fixture.Home, "session_browser_trash"); Directory.CreateDirectory(trashRoot);
        using var operation = new FileStream(Path.Combine(trashRoot, ".ezmate-files.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => fixture.Store.Apply(session, BrowserFileAction.Trash, default));
        Assert.True(File.Exists(session.Path));
    }

    [Fact]
    public void OversizedLineIsSkipped_AndEventOnlyPreviewStillWorks()
    {
        using var fixture = new Fixture(); var session = fixture.Session();
        File.WriteAllText(session.Path, new string('x', 2 * 1024 * 1024 + 10) + "\n" + JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "user_message", message = "Fallback" } }));
        Assert.Equal("Fallback", Assert.Single(fixture.Store.ReadPreview(session, default).Messages).Text);
    }

    [Fact]
    public void LegacySettingsRoundTrip_PreservesFilterWidthsAndUnknownFields()
    {
        using var fixture = new Fixture(); var path = fixture.Store.SettingsPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"geometry\":\"1100x860-100+88\",\"filter\":\"Deleted only\",\"hide_internal\":true,\"columns\":{\"name\":164},\"future\":42}", new UTF8Encoding(true));
        var settings = SessionBrowserSettings.Load(path); Assert.Equal("Deleted only", settings.Filter); Assert.True(settings.HideInternal); Assert.Equal(164, settings.Columns["name"]);
        settings.Save(path); var reloaded = SessionBrowserSettings.Load(path); Assert.Equal(42, reloaded.Extra!["future"].GetInt32()); Assert.Equal(settings.Geometry, reloaded.Geometry);
    }

    [Theory]
    [InlineData(AppLanguage.TraditionalChinese)]
    [InlineData(AppLanguage.SimplifiedChinese)]
    [InlineData(AppLanguage.English)]
    public void WpfWindow_RendersAndChangesLanguageWithoutRewritingConversation(AppLanguage language)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var fixture = new Fixture();
                System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                var window = new SessionBrowserWindow(fixture.Store);
                var item = fixture.Session(); item.Name = "保持原本的會話名稱";
                window.SetSessions(new BrowserScan([item], 0)); window.SessionsGrid.SelectedItem = item;
                var previewTask = window.PreviewAsync();
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                var started = System.Diagnostics.Stopwatch.StartNew();
                timer.Tick += (_, _) => { if (previewTask.IsCompleted || started.Elapsed.TotalSeconds > 5) frame.Continue = false; };
                timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
                Assert.True(previewTask.IsCompletedSuccessfully); Assert.Contains("Hello world", window.PreviewText);
                var originalPreview = window.PreviewText;
                window.SetLanguage(language);
                Assert.Same(item, window.SessionsGrid.SelectedItem); Assert.Equal(originalPreview, window.PreviewText);
                Assert.Equal("保持原本的會話名稱", item.Name); Assert.Equal(8, window.SessionsGrid.Columns.Count);
                Assert.Equal(language == AppLanguage.English ? "Updated" : language == AppLanguage.SimplifiedChinese ? "更新时间" : "更新時間", window.SessionsGrid.Columns[0].Header);
                // Use an English sample title for the English documentation screenshot.
                if (language == AppLanguage.English)
                {
                    item.Name = "Example: Review project notes";
                    window.SetLanguage(language);
                }
                // Render a stable example path instead of the machine-specific fixture directory.
                var rootPanel = (System.Windows.Controls.DockPanel)window.Content;
                foreach (var block in rootPanel.Children.OfType<System.Windows.Controls.StackPanel>().SelectMany(p => p.Children.OfType<System.Windows.Controls.TextBlock>()))
                    block.Text = block.Text.Replace(fixture.Home, @"C:\Users\Example\.codex");
                var content = (FrameworkElement)window.Content; content.Measure(new Size(1240, 740)); content.Arrange(new Rect(0, 0, 1240, 740)); content.UpdateLayout();
                var output = Environment.GetEnvironmentVariable("CODEX_UI_PREVIEW_ROOT");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output); var bitmap = new RenderTargetBitmap(1240, 740, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, $"session-browser-dotnet-{language}.png")); encoder.Save(stream);
                }
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20))); if (failure is not null) throw failure;
    }
}

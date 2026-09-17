using System.Text.Json;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Xunit;
using Xunit.Abstractions;

namespace CodexUsageAssistant.Tests;

public sealed class GoalIntegrationTests(ITestOutputHelper output)
{
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Scan_ReadsAllPagesAndDeduplicatesByThreadId()
    {
        var calls = new List<string?>();
        var result = await ConversationDiscoveryService.ReadAllPagesAsync((cursor, _) =>
        {
            calls.Add(cursor);
            return Task.FromResult(cursor is null ? Json("""
                {"data":[{"id":"01900000-0000-7000-8000-000000000001","name":"First goal","cwd":"C:\\One","section":{"name":"My project"}}],"nextCursor":"page2"}
                """) : Json("""
                {"data":[{"id":"01900000-0000-7000-8000-000000000001","name":"Renamed goal","cwd":"C:\\One","section":{"name":"My project"}},
                {"id":"01900000-0000-7000-8000-000000000002","name":"Second goal","cwd":"C:\\Two"}],"nextCursor":null}
                """));
        }, CancellationToken.None);
        Assert.Equal(new string?[] { null, "page2" }, calls);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, row => row.Title == "Renamed goal" && row.ProjectName == "My project");
        Assert.Contains(result, row => row.Title == "Second goal" && row.ProjectName == "Two");
        Assert.All(result, row => Assert.False(row.Enabled));
    }

    [Fact]
    public async Task Scan_RepeatedCursorFailsInsteadOfSavingPartialList()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationDiscoveryService.ReadAllPagesAsync(
            (_, _) => Task.FromResult(Json("""{"data":[],"nextCursor":"same"}""")), CancellationToken.None));
    }

    [Fact]
    public async Task Scan_DuplicateTitlesRemainDistinctByThreadId()
    {
        var result = await ConversationDiscoveryService.ReadAllPagesAsync((_, _) => Task.FromResult(Json("""
            {"data":[{"id":"01900000-0000-7000-8000-000000000001","name":"Same","cwd":"C:\\One"},
            {"id":"01900000-0000-7000-8000-000000000002","name":"Same","cwd":"C:\\Two"}],"nextCursor":null}
            """)), CancellationToken.None);
        Assert.All(result, row => Assert.Equal(ConversationIdentityStatus.Available, row.IdentityStatus));
    }

    [Fact]
    public async Task Scan_InvalidExecutableReturnsFailureNotAnEmptySuccessfulScan()
    {
        var service = new ConversationDiscoveryService(new Options { Value = new() { CodexExecutablePath = "missing.exe" } });
        var result = await service.ScanAsync(CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public void ThreadIdentitySurvivesTitleAndProjectRename()
    {
        var target = new ConversationTarget { ThreadId = "01900000-0000-7000-8000-000000000001", Title = "Original", ProjectName = "One" };
        var before = target.Key;
        target.Title = "Renamed";
        target.ProjectName = "Two";
        Assert.Equal(before, target.Key);
    }

    [Fact]
    public void SimplifiedChinese_ConvertsDynamicMessagesWithoutChangingEnglish()
    {
        Assert.Equal("扫描对话，设置已保存", LocalizationService.Translate("掃描對話，設置已保存", "Settings saved", AppLanguage.SimplifiedChinese));
        Assert.Equal("Settings saved", LocalizationService.Translate("掃描對話", "Settings saved", AppLanguage.English));
        Assert.Equal(1, (int)AppLanguage.English);
        var saved = JsonSerializer.Serialize(new WindowPosition { Language = AppLanguage.SimplifiedChinese });
        Assert.Equal(AppLanguage.SimplifiedChinese, JsonSerializer.Deserialize<WindowPosition>(saved)!.Language);
    }

    [Fact]
    public async Task LiveGoalScan_WhenEnabled_ReadsMetadataOnly()
    {
        if (Environment.GetEnvironmentVariable("CODEX_TEST_LIVE_GOAL_SCAN") != "1") return;
        var result = await new ConversationDiscoveryService(new JsonSettingsService()).ScanAsync(CancellationToken.None);
        output.WriteLine($"Scan success={result.Success}; conversations={result.Conversations.Count}; identified={result.Conversations.Count(row => row.ThreadId is not null)}");
        Assert.True(result.Success, result.Message);
        Assert.NotEmpty(result.Conversations);
        Assert.All(result.Conversations, row => Assert.False(row.Enabled));
    }

    [Fact]
    public async Task LiveResetRead_WhenEnabled_ReceivesCountAndDetails()
    {
        if (Environment.GetEnvironmentVariable("CODEX_TEST_LIVE_GOAL_SCAN") != "1") return;
        var result = await new AppServerUsageService().ReadAsync(null, CancellationToken.None);
        output.WriteLine($"Reset count={result.AvailableUsageResetCount}; expirations={string.Join(", ", result.UsageLimitResets.Select(row => row.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")))}");
        Assert.Equal(UsageStatus.Available, result.Status);
        Assert.NotNull(result.AvailableUsageResetCount);
        Assert.NotEmpty(result.UsageLimitResets);
    }

    private sealed class Options : ISettingsService
    {
        public WindowPosition Value { get; set; } = new();
        public Task<WindowPosition?> LoadAsync(CancellationToken token) => Task.FromResult<WindowPosition?>(Value);
        public Task SaveAsync(WindowPosition value, CancellationToken token) { Value = value; return Task.CompletedTask; }
    }
}

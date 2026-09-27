using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Xunit;

namespace CodexUsageAssistant.Tests.Regression;

public sealed class ResetIncreaseTests
{
    private static UsageData Data(int? count, int time, string source = "App Server", UsageStatus status = UsageStatus.Available) =>
        new() { AvailableUsageResetCount = count, LastUpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(time), DataSource = source, Status = status };
    [Fact]
    public void NotifiesOnlyFreshIncreasesAndTracksRedemption()
    {
        var tracker = new ResetIncreaseTracker();
        Assert.Equal(0, tracker.Observe(Data(2, 1)));
        Assert.Equal(1, tracker.Observe(Data(3, 2)));
        Assert.Equal(0, tracker.Observe(Data(3, 2)));
        Assert.Equal(0, tracker.Observe(Data(3, 3)));
        Assert.Equal(0, tracker.Observe(Data(1, 1)));
        Assert.Equal(0, tracker.Observe(Data(2, 4)));
        Assert.Equal(1, tracker.Observe(Data(3, 5)));
    }
    [Fact]
    public void MissingFailedAndChangedSourcesDoNotCreateFalseIncreases()
    {
        var tracker = new ResetIncreaseTracker();
        Assert.Equal(0, tracker.Observe(Data(2, 1)));
        Assert.Equal(0, tracker.Observe(Data(null, 2)));
        Assert.Equal(0, tracker.Observe(Data(0, 3, status: UsageStatus.NetworkError)));
        Assert.Equal(0, tracker.Observe(Data(2, 4)));
        Assert.Equal(0, tracker.Observe(Data(5, 5, "DOM")));
        Assert.Equal(1, tracker.Observe(Data(6, 6, "DOM")));
        tracker.Reset();
        Assert.Equal(0, tracker.Observe(Data(8, 7, "DOM")));
    }
}

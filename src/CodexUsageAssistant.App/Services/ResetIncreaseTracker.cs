using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

internal sealed class ResetIncreaseTracker
{
    private int? _count;
    private string? _source;
    private DateTimeOffset _timestamp;
    internal void Reset() { _count = null; _source = null; _timestamp = default; }
    internal int Observe(UsageData usage)
    {
        if (usage.Status != UsageStatus.Available || usage.AvailableUsageResetCount is not int count || count < 0) return 0;
        var source = usage.DataSource ?? "";
        if (_source != source) { Reset(); _source = source; }
        if (usage.LastUpdatedAt != default && _timestamp != default && usage.LastUpdatedAt <= _timestamp) return 0;
        var increase = _count is int previous ? Math.Max(0, count - previous) : 0;
        _count = count;
        if (usage.LastUpdatedAt != default) _timestamp = usage.LastUpdatedAt;
        return increase;
    }
}

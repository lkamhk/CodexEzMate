namespace CodexUsageAssistant.Models;

public sealed class AutomationProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool RequireConfirmation { get; set; }
    public TargetApplication Target { get; set; } = new();
    public List<AutomationStep> Steps { get; set; } = [];
}

public sealed class TargetApplication
{
    public string ProcessName { get; set; } = string.Empty;
    public string? WindowTitleContains { get; set; }
}

public sealed class AutomationStep
{
    public string Type { get; set; } = string.Empty;
    public string? AutomationId { get; set; }
    public string? Name { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Milliseconds { get; set; }
    public int TimeoutMilliseconds { get; set; } = 10000;
    public bool ContinueOnError { get; set; }
}

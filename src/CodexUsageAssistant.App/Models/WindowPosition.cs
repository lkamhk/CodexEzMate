namespace CodexUsageAssistant.Models;

public enum UsageDisplayMode
{
    FloatingBall,
    SystemTray,
    Both
}

public enum AppLanguage
{
    TraditionalChinese,
    English,
    SimplifiedChinese
}

public enum UsageReadMode { AppServerWithDomFallback, AppServerOnly, DomOnly }

public sealed class WindowPosition
{
    public bool HotkeysEnabled { get; set; } = true;
    public List<HotkeyBinding> Hotkeys { get; set; } = [];
    public bool AutoCheckUpdates { get; set; } = true;
    public bool TrayShowRemainingUsage { get; set; }
    public bool HostAutoStart { get; set; } = true;
    public string? HostExecutablePath { get; set; }
    public int HostPort { get; set; } = 4500;
    public string? HostWorkingDirectory { get; set; }
    public int DetailsContentTab { get; set; }
    public bool RefreshOnServerNotification { get; set; }
    public UsageReadMode UsageReadMode { get; set; } = UsageReadMode.AppServerWithDomFallback;
    public string? CodexExecutablePath { get; set; }
    public string MonitorDeviceName { get; set; } = string.Empty;
    public double Left { get; set; }
    public double Top { get; set; }
    public double DpiScale { get; set; } = 1.0;
    public string? ProxyServer { get; set; }
    public string? ProxyBypassList { get; set; }
    public bool ProxyEnabled { get; set; }
    public string? ProxyUsername { get; set; }
    public string? EncryptedProxyPassword { get; set; }
    public int? AutoRefreshIntervalMinutes { get; set; } = 5;
    public UsageDisplayMode DisplayMode { get; set; } = UsageDisplayMode.Both;
    public AppLanguage Language { get; set; } = AppLanguage.TraditionalChinese;
    public bool UseSavedResumeCoordinates { get; set; }
    public int? ResumeClickX { get; set; }
    public int? ResumeClickY { get; set; }
}

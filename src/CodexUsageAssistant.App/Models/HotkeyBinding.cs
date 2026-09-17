namespace CodexUsageAssistant.Models;

public enum HotkeyAction { SessionBrowser, GoalMonitor, ServerHost, UsageDetails, Settings, Updates, HotkeySettings }

public sealed record HotkeyBinding(HotkeyAction Action, uint Modifiers, uint VirtualKey);

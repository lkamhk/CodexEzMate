using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.Models;

public sealed class BrowserSession : System.ComponentModel.INotifyPropertyChanged
{
    private AppLanguage _language = LocalizationService.CurrentLanguage;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void SetLanguage(AppLanguage language)
    {
        _language = language;
        PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(CreatorDisplay)));
    }
    private string L(string chinese, string english) => LocalizationService.Translate(chinese, english, _language);
    public string Path { get; init; } = "";
    public IReadOnlyList<BrowserSessionPart> Parts { get; init; } = [];
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameSource { get; set; } = "";
    public string Preview { get; set; } = "";
    public string Project { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Source { get; set; } = "";
    public string Originator { get; set; } = "";
    public string CreatorDisplay => Originator.Trim().ToLowerInvariant() switch
    {
        "codex desktop" or "codex_desktop" => "Codex",
        "codex_vscode" => "VS Code",
        "codex_cli_rs" or "codex_cli" => "CLI",
        _ => Source.Trim().ToLowerInvariant() switch
        {
            "cli" => "CLI",
            "desktop" => "Codex",
            _ => L("未知", "Unknown")
        }
    };
    public bool Internal { get; set; }
    public bool Archived { get; init; }
    public bool Deleted { get; init; }
    public bool Backup { get; init; }
    public DateTime Updated { get; init; }
    public long Size { get; init; }
    internal DateTime Created { get; init; }
    internal string HistoryBaseThreadId { get; set; } = "";
    internal long HistoryBaseEndByteOffset { get; set; }
    internal string StorageGroup { get; set; } = "";
    public string DisplayName => string.IsNullOrEmpty(Name) ? L("未命名", "Unnamed") : Name;
    public string Status => Backup ? L("備份", "Backup") + (Archived ? " / " + L("已封存", "Archived") : "")
        : Deleted ? L("回收區", "Trash") + (Archived ? " / " + L("已封存", "Archived") : "")
        : Archived ? L("已封存", "Archived") : L("使用中", "Active");
    public string SizeText => Size >= 1048576 ? $"{Size / 1048576.0:0.0} MB" : Size >= 1024 ? $"{Size / 1024.0:0.0} KB" : $"{Size} B";
}

public sealed record BrowserSessionPart(string Path, long Size, DateTime Updated, long? MaxReadLength = null);
public sealed record BrowserMessage(string Timestamp, string Role, string Text);
public sealed record BrowserPreview(IReadOnlyList<BrowserMessage> Messages, bool Truncated);
public sealed record BrowserPreviewCursor(string Path, long Length, long WrittenTicks, int Message, int Character);
public sealed record BrowserPreviewPart(string Role, string Timestamp, string Text, bool Continued);
public sealed record BrowserPreviewPage(string Text, BrowserPreviewCursor? Next, bool Reloaded, bool Truncated)
{
    public IReadOnlyList<BrowserPreviewPart> Parts { get; init; } = [];
}
public sealed record BrowserScan(IReadOnlyList<BrowserSession> Sessions, int Skipped);
public enum BrowserFileAction { Backup, Trash, Restore, Recycle }

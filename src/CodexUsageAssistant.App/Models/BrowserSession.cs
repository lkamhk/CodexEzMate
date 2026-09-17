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
    }
    private string L(string chinese, string english) => LocalizationService.Translate(chinese, english, _language);
    public string Path { get; init; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameSource { get; set; } = "";
    public string Preview { get; set; } = "";
    public string Project { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Internal { get; set; }
    public bool Archived { get; init; }
    public bool Deleted { get; init; }
    public bool Backup { get; init; }
    public DateTime Updated { get; init; }
    public long Size { get; init; }
    public string DisplayName => string.IsNullOrEmpty(Name) ? L("未命名", "Unnamed") : Name;
    public string Status => Backup ? L("備份", "Backup") + (Archived ? " / " + L("已封存", "Archived") : "")
        : Deleted ? L("已刪除", "Deleted") + (Archived ? " / " + L("已封存", "Archived") : "")
        : Archived ? L("已封存", "Archived") : L("使用中", "Active");
    public string SizeText => Size >= 1048576 ? $"{Size / 1048576.0:0.0} MB" : Size >= 1024 ? $"{Size / 1024.0:0.0} KB" : $"{Size} B";
}

public sealed record BrowserMessage(string Timestamp, string Role, string Text);
public sealed record BrowserPreview(IReadOnlyList<BrowserMessage> Messages, bool Truncated);
public sealed record BrowserScan(IReadOnlyList<BrowserSession> Sessions, int Skipped);
public enum BrowserFileAction { Backup, Trash, Restore }

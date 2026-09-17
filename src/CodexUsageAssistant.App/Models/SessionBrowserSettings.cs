using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.Models;

public sealed class SessionBrowserSettings
{
    public static readonly string[] Filters = ["All sessions", "Active only", "Archived only", "Deleted only", "Backups only"];
    [JsonPropertyName("geometry")] public string Geometry { get; set; } = "1280x820+80+80";
    [JsonPropertyName("filter")] public string Filter { get; set; } = "All sessions";
    [JsonPropertyName("hide_internal")] public bool HideInternal { get; set; }
    [JsonPropertyName("columns")] public Dictionary<string, double> Columns { get; set; } = [];
    [JsonPropertyName("search")] public string Search { get; set; } = "";
    [JsonPropertyName("maximized")] public bool Maximized { get; set; }
    [JsonPropertyName("sort_column")] public string SortColumn { get; set; } = "updated";
    [JsonPropertyName("sort_descending")] public bool SortDescending { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static SessionBrowserSettings Load(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            var settings = JsonSerializer.Deserialize<SessionBrowserSettings>(reader.ReadToEnd()) ?? new();
            if (!Filters.Contains(settings.Filter)) settings.Filter = Filters[0];
            settings.Columns ??= []; settings.Search ??= "";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string path) => SessionBrowserStore.WriteJson(path, this);

    public static bool Matches(BrowserSession session, string query, string filter, bool hideInternal)
    {
        if (hideInternal && session.Internal) return false;
        if (filter == "Active only" && (session.Archived || session.Deleted || session.Backup)) return false;
        if (filter == "Archived only" && (!session.Archived || session.Deleted || session.Backup)) return false;
        if (filter == "Deleted only" && !session.Deleted) return false;
        if (filter == "Backups only" && !session.Backup) return false;
        return string.IsNullOrWhiteSpace(query) || new[] { session.Id, session.Name, session.Preview, session.Project, session.Provider, session.Path }
            .Any(value => value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

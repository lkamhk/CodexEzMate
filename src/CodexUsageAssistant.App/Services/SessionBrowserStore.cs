using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexUsageAssistant.Models;
using Microsoft.Data.Sqlite;

namespace CodexUsageAssistant.Services;

public sealed class SessionBrowserStore
{
    public string CodexHome { get; }
    public string BackupRoot { get; }
    public string SettingsPath { get; }
    private string Active => System.IO.Path.Combine(CodexHome, "sessions");
    private string Archived => System.IO.Path.Combine(CodexHome, "archived_sessions");
    private string Trash => System.IO.Path.Combine(CodexHome, "session_browser_trash");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lazy<bool> SqliteReady = new(() => { SQLitePCL.Batteries_V2.Init(); return true; });

    public SessionBrowserStore(string? codexHome = null, string? dataRoot = null)
    {
        var home = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        if (home == "~" || home.StartsWith("~/") || home.StartsWith("~\\")) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + home[1..];
        CodexHome = System.IO.Path.GetFullPath(home);
        dataRoot ??= ResolveDataRoot(AppContext.BaseDirectory);
        BackupRoot = System.IO.Path.Combine(dataRoot, "codexSessionBrowser", "codex_session_bkup");
        SettingsPath = System.IO.Path.Combine(dataRoot, "settings", "session-browser.json");
    }

    internal static string ResolveDataRoot(string baseDirectory)
    {
        var root = InstallationPaths.ResolveRoot(baseDirectory);
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "CodexUsageAssistant.sln"))) return directory.FullName;
        return root;
    }

    internal static JsonElement Property(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : default;
    internal static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string Get(JsonElement value, string key) => Text(Property(value, key));
    private static string First(JsonElement value, params string[] keys) => keys.Select(k => Get(value, k)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
    private static string Clean(string value, int maximum = 500) { var text = Regex.Replace(value, @"\s+", " ").Trim(); return text[..Math.Min(text.Length, maximum)]; }
    internal static bool IsInternal(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? value.TryGetProperty("subagent", out _) || value.TryGetProperty("subAgent", out _)
        : Text(value).Trim().Replace("_", "").ToLowerInvariant() is var source && (source.StartsWith("subagent") || source == "guardian");

    // Bound each line independently so a large tool payload never becomes a giant allocation.
    private static IEnumerable<JsonElement> Records(string path, CancellationToken token, int maxLines = int.MaxValue, long maxCharacters = long.MaxValue)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 16384);
        var buffer = new char[16384]; var line = new StringBuilder(); var overflow = false; var lines = 0; long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, maxCharacters - total));
            if (count == 0) break;
            total += count;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n') { if (line.Length < 2 * 1024 * 1024) line.Append(buffer[i]); else overflow = true; continue; }
                var parsed = overflow ? default : Parse(line.ToString());
                line.Clear(); overflow = false;
                if (parsed.ValueKind == JsonValueKind.Object) yield return parsed;
                if (++lines >= maxLines) yield break;
            }
            if (total >= maxCharacters) yield break;
        }
        if (!overflow && line.Length != 0) { var parsed = Parse(line.ToString()); if (parsed.ValueKind == JsonValueKind.Object) yield return parsed; }
    }

    private static JsonElement Parse(string value)
    {
        try { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    private static IEnumerable<string> Rollouts(string root) => !Directory.Exists(root) ? [] : Directory.EnumerateFiles(root, "rollout-*.jsonl",
        new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });

    public BrowserScan Scan(CancellationToken token)
    {
        var names = LoadNames(token); var found = new List<BrowserSession>(); var skipped = 0;
        foreach (var entry in new[] { (Active, false, false, false), (Archived, true, false, false),
            (System.IO.Path.Combine(Trash, "active"), false, true, false), (System.IO.Path.Combine(Trash, "archived"), true, true, false),
            (System.IO.Path.Combine(BackupRoot, "active"), false, false, true), (System.IO.Path.Combine(BackupRoot, "archived"), true, false, true) })
        {
            if (!Directory.Exists(entry.Item1)) continue;
            try
            {
                EnsureNoLinks(entry.Item1);
                foreach (var path in Rollouts(entry.Item1))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var info = ReadHead(path, entry.Item2, entry.Item3, entry.Item4, token);
                        if (names.TryGetValue(info.Id, out var name)) { info.Name = name.Name; info.NameSource = name.Source; }
                        found.Add(info);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new BrowserScan(found.OrderByDescending(s => s.Updated).ThenBy(s => s.Name).ThenBy(s => s.Id).ToList(), skipped);
    }

    internal Dictionary<string, (string Name, string Source)> LoadNames(CancellationToken token)
    {
        var names = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(CodexHome))
        {
            _ = SqliteReady.Value;
            var databases = Directory.EnumerateFiles(CodexHome, "state*.sqlite").Where(p => Regex.IsMatch(System.IO.Path.GetFileName(p), @"^state(?:_\d+)?\.sqlite$"))
                .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(p => int.TryParse(Regex.Match(System.IO.Path.GetFileName(p), @"\d+").Value, out var version) ? version : -1);
            foreach (var database in databases)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    EnsureNoLinks(database);
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
                    connection.Open();
                    using var schema = connection.CreateCommand(); schema.CommandText = "PRAGMA table_info(threads)";
                    var columns = new HashSet<string>();
                    using (var rows = schema.ExecuteReader()) while (rows.Read()) columns.Add(rows.GetString(1));
                    var id = new[] { "id", "thread_id", "session_id" }.FirstOrDefault(columns.Contains);
                    var title = new[] { "title", "thread_name", "name" }.FirstOrDefault(columns.Contains);
                    if (id is null || title is null) continue;
                    using var query = connection.CreateCommand(); query.CommandText = $"SELECT \"{id}\", \"{title}\" FROM threads";
                    using var reader = query.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                        var key = reader.GetString(0).Trim(); var name = Clean(reader.GetString(1));
                        if (key.Length > 0 && name.Length > 0) names.TryAdd(key, (name, System.IO.Path.GetFileName(database) + ":threads." + title));
                    }
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { }
            }
        }
        try
        {
            var index = System.IO.Path.Combine(CodexHome, "session_index.jsonl");
            if (!File.Exists(index)) return names;
            foreach (var record in Records(index, token))
            {
                var value = First(record, "id", "thread_id", "session_id").Length != 0 ? record : Property(record, "payload");
                var id = First(value, "id", "thread_id", "session_id").Trim();
                if (id.Length == 0 || value.ValueKind != JsonValueKind.Object) continue;
                var key = new[] { "thread_name", "session_name", "conversation_name", "title" }.FirstOrDefault(k => value.TryGetProperty(k, out _));
                if (key is null) continue;
                var name = Clean(Get(value, key));
                if (name.Length == 0) names.Remove(id); else names[id] = (name, "session_index.jsonl");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return names;
    }

    private BrowserSession ReadHead(string path, bool archived, bool deleted, bool backup, CancellationToken token)
    {
        var file = new FileInfo(path);
        var info = new BrowserSession { Path = path, Archived = archived, Deleted = deleted, Backup = backup, Size = file.Length, Updated = file.LastWriteTime,
            Id = Regex.Match(file.Name, @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}").Value };
        string response = "", fallback = ""; var recordCount = 0;
        foreach (var record in Records(path, token, 300, 8 * 1024 * 1024))
        {
            var payload = Property(record, "payload");
            if (Get(record, "type") == "session_meta")
            {
                var meta = Property(payload, "meta"); if (meta.ValueKind != JsonValueKind.Object) meta = payload;
                var id = First(meta, "id", "session_id"); if (id.Length != 0) info.Id = id;
                info.Project = Get(meta, "cwd"); info.Provider = Get(meta, "model_provider");
                var source = Property(meta, "source"); info.Source = source.ValueKind == JsonValueKind.Object ? source.GetRawText() : Text(source);
                info.Internal = IsInternal(source); info.Name = Clean(First(meta, "thread_name", "session_name", "conversation_name", "title"));
                if (info.Name.Length > 0) info.NameSource = "rollout session_meta";
            }
            var message = Response(record);
            if (response.Length == 0 && message?.Role == "user") response = message.Text;
            if (fallback.Length == 0) fallback = EventText(record);
            if (++recordCount >= 6 && info.Id.Length != 0 && info.Project.Length != 0 && (response.Length != 0 || fallback.Length != 0)) break;
        }
        info.Preview = Clean(response.Length != 0 ? response : fallback, 400);
        return info;
    }

    private static BrowserMessage? Response(JsonElement record)
    {
        var payload = Property(record, "payload"); var role = Get(payload, "role");
        if (Get(record, "type") != "response_item" || Get(payload, "type") != "message" || role is not ("user" or "assistant")) return null;
        var content = Property(payload, "content");
        var text = content.ValueKind == JsonValueKind.Array ? string.Join("\n", content.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? Text(c) : First(c, "text", "content")).Where(s => !string.IsNullOrWhiteSpace(s))) : Text(content);
        return string.IsNullOrWhiteSpace(text) ? null : new BrowserMessage(Get(record, "timestamp"), role, text.Trim());
    }
    private static string EventText(JsonElement record)
    {
        var payload = Property(record, "payload");
        return Get(record, "type") == "event_msg" && Get(payload, "type") == "user_message" ? Get(payload, "message") : "";
    }

    public BrowserPreview ReadPreview(BrowserSession session, CancellationToken token)
    {
        ValidateSource(session);
        var responses = new List<BrowserMessage>(); var events = new List<BrowserMessage>();
        var truncated = new FileInfo(session.Path).Length > 32 * 1024 * 1024; var characters = 0;
        foreach (var record in Records(session.Path, token, maxCharacters: 32 * 1024 * 1024))
        {
            var message = Response(record);
            if (message is not null) { responses.Add(message); characters += message.Text.Length; }
            else if (events.Count < 1000 && EventText(record) is { Length: > 0 } text) { events.Add(new(Get(record, "timestamp"), "user", text)); characters += text.Length; }
            if (responses.Count >= 1000 || characters >= 4 * 1024 * 1024) { truncated = true; break; }
        }
        return new BrowserPreview(responses.Count != 0 ? responses : events, truncated);
    }

    internal static void EnsureNoLinks(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        for (string? current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported for session file operations.");
    }
    private static string RequireWithin(string path, string root)
    {
        var full = System.IO.Path.GetFullPath(path); var prefix = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Session path is outside the expected directory.");
        EnsureNoLinks(full); return full;
    }
    private string OriginalRoot(BrowserSession session) => session.Archived ? Archived : Active;
    private string StoredRoot(BrowserSession session) => session.Backup ? System.IO.Path.Combine(BackupRoot, session.Archived ? "archived" : "active")
        : session.Deleted ? System.IO.Path.Combine(Trash, session.Archived ? "archived" : "active") : OriginalRoot(session);
    public void ValidateSource(BrowserSession session)
    {
        RequireWithin(session.Path, StoredRoot(session));
        if (!File.Exists(session.Path)) throw new FileNotFoundException("Session file no longer exists.");
    }

    public string Apply(BrowserSession session, BrowserFileAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ValidateSource(session);
        EnsureNoLinks(Trash); Directory.CreateDirectory(Trash);
        using var operationLock = new FileStream(System.IO.Path.Combine(Trash, ".ezmate-files.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (action == BrowserFileAction.Restore) return Restore(session, token);
        if (session.Deleted || session.Backup) throw new IOException("Only active or archived sessions can be backed up or moved.");
        var backup = action == BrowserFileAction.Backup;
        var relative = System.IO.Path.GetRelativePath(OriginalRoot(session), session.Path);
        var root = System.IO.Path.Combine(backup ? BackupRoot : Trash, session.Archived ? "archived" : "active");
        var destination = RequireWithin(System.IO.Path.Combine(root, relative), root);
        var suffix = backup ? ".bkup.json" : ".trash.json";
        if (File.Exists(destination) || File.Exists(destination + suffix))
        {
            var original = destination; var number = 0; var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-ffffff");
            do { destination = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(original)!, System.IO.Path.GetFileNameWithoutExtension(original) + "__" + stamp + (number++ == 0 ? "" : "_" + number) + ".jsonl"); }
            while (File.Exists(destination) || File.Exists(destination + suffix));
        }
        var manifest = new Dictionary<string, object> { ["version"] = 1, ["session_id"] = session.Id, ["session_name"] = session.Name,
            [backup ? "backed_up_at" : "deleted_at"] = DateTimeOffset.Now.ToString("O"), ["original_kind"] = session.Archived ? "archived" : "active",
            ["relative_path"] = relative.Replace('\\', '/'), ["original_path"] = session.Path };
        WriteJson(destination + suffix, manifest, overwrite: false);
        try
        {
            if (backup) CopySnapshot(session.Path, destination, token);
            else
            {
                using var guard = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                token.ThrowIfCancellationRequested(); File.Move(session.Path, destination, false);
            }
        }
        catch { File.Delete(destination + suffix); throw; }
        return destination;
    }

    private string Restore(BrowserSession session, CancellationToken token)
    {
        if (!session.Backup && !session.Deleted) throw new IOException("Only deleted sessions or backups can be restored.");
        var root = OriginalRoot(session); var suffix = session.Backup ? ".bkup.json" : ".trash.json";
        var manifestPath = session.Path + suffix; string? destination = null;
        if (File.Exists(manifestPath))
        {
            EnsureNoLinks(manifestPath);
            if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new IOException("Restore metadata is too large.");
            using var reader = new StreamReader(manifestPath, Encoding.UTF8, true);
            var manifest = Parse(reader.ReadToEnd());
            if (manifest.ValueKind != JsonValueKind.Object) throw new IOException("Restore metadata is invalid.");
            var kind = Get(manifest, "original_kind"); var relative = Get(manifest, "relative_path");
            if (kind.Length > 0 && kind != (session.Archived ? "archived" : "active")) throw new IOException("Restore metadata does not match the session category.");
            if (relative.Length > 0)
            {
                if (System.IO.Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Contains("..")) throw new IOException("Unsafe restore path.");
                destination = RequireWithin(System.IO.Path.Combine(root, relative), root);
            }
            else if (session.Deleted && Get(manifest, "original_path") is { Length: > 0 } original) destination = RequireWithin(original, root);
        }
        if (destination is null)
        {
            var relative = System.IO.Path.GetRelativePath(StoredRoot(session), session.Path);
            if (session.Backup) relative = Regex.Replace(relative, @"__\d{8}-\d{6}-\d{6}(?:_\d+)?(?=\.jsonl$)", "");
            destination = RequireWithin(System.IO.Path.Combine(root, relative), root);
        }
        if (File.Exists(destination)) throw new IOException("Restore stopped: the destination already exists.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        if (session.Backup) CopySnapshot(session.Path, destination, token);
        else
        {
            using var guard = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            token.ThrowIfCancellationRequested(); File.Move(session.Path, destination, false);
            try { File.Delete(manifestPath); } catch (IOException) { }
        }
        return destination;
    }

    private static void CopySnapshot(string source, string destination, CancellationToken token)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; int read;
                while ((read = input.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                output.Flush(true);
            }
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void WriteJson<T>(string path, T value, bool overwrite = true)
    {
        EnsureNoLinks(path); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false)); File.Move(temporary, path, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

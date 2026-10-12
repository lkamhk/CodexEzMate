using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexUsageAssistant.Services;

internal static class SessionNameFormatter
{
    internal static string Shorten(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var elements = StringInfo.ParseCombiningCharacters(text);
        return elements.Length <= 5 ? text : text[..elements[5]] + "…";
    }

    internal static string UserText(string text) => Shorten(CleanUserText(text));

    internal static string CleanUserText(string text)
    {
        text = text.Trim();
        // These envelopes are injected context, not instructions for this parser.
        var request = text.IndexOf("## My request:", StringComparison.Ordinal);
        if (request >= 0 && (text.StartsWith("# Files mentioned by the user:", StringComparison.Ordinal) ||
            text.StartsWith("<environment_context>", StringComparison.Ordinal))) text = text[(request + 14)..].Trim();
        foreach (var prefix in new[] { "<environment_context>", "<permissions instructions>", "<skills_instructions>",
            "<recommended_plugins>", "<INSTRUCTIONS>", "# AGENTS.md instructions", "<developer_instructions>",
            "<turn_aborted>", "<subagent_notification>", "<send_user_message_question_reply>",
            "The following is the Codex agent history", "You are performing a CONTEXT CHECKPOINT", "[Codex EzMate " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
        // Ignore known attachment blocks, keeping surrounding user-authored text.
        text = Regex.Replace(text, @"<image\b[^>]*>.*?</image>|<image\b[^>]*/>", "", RegexOptions.Singleline);
        if (text.StartsWith('<')) return "";
        return Models.SessionNamingRules.Limit(text, 4096);
    }
}

internal sealed record AutoNameCandidate(string ThreadId, string Name, long EndOffset,
    string Project = "", string FirstPrompt = "", string LatestPrompt = "");

internal sealed class SessionAutoNameObserver
{
    private readonly IncrementalJsonlReader _reader = new();
    private string _id = "", _project = "", _first = "", _latest = "", _turn = "", _fallback = "";
    private int _count;
    private bool _internal, _active, _canonical;
    private string _completedTurn = "";
    private string _lastEvent = "";
    private string _fallbackFirst = "";
    private int _fallbackCount;
    internal long Offset => _reader.Offset;

    internal AutoNameCandidate? Read(string path, long cutoff, CancellationToken token)
    {
        AutoNameCandidate? candidate = null;
        var reset = false;
        var previouslyRead = Offset > 0;
        _reader.Read(path, (root, start, end) =>
        {
            var type = Get(root, "type");
            var payload = SessionBrowserStore.Property(root, "payload");
            if (type == "session_meta")
            {
                var meta = SessionBrowserStore.Property(payload, "meta");
                if (meta.ValueKind != JsonValueKind.Object) meta = payload;
                _id = Get(meta, "id");
                _project = ProjectName(Get(meta, "cwd"));
                _internal = SessionBrowserStore.IsInternal(SessionBrowserStore.Property(meta, "source"));
                return;
            }
            if (_internal) return;
            var kind = Get(payload, "type");
            if (type == "event_msg")
            {
                if (kind == "task_started")
                {
                    _active = true; _turn = Get(payload, "turn_id"); _canonical = false; ClearFallback(); _lastEvent = "";
                    candidate = null;
                }
                else if (kind == "user_message")
                {
                    var raw = Get(payload, "message");
                    var signature = Get(root, "timestamp") + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
                    if (_lastEvent == signature) return;
                    _lastEvent = signature;
                    var text = SessionNameFormatter.CleanUserText(raw);
                    if (text.Length > 0) { Add(text); _canonical = true; ClearFallback(); }
                }
                else if (kind is "task_failed" or "turn_aborted" or "task_aborted" or "error")
                { _active = false; ClearFallback(); candidate = null; }
                else if (kind == "task_complete")
                {
                    var completed = Get(payload, "turn_id");
                    var valid = _active && completed.Length > 0 && completed == _turn && completed != _completedTurn;
                    if (!valid) return;
                    if (!_canonical && _fallbackCount > 0)
                    { Add(_fallbackFirst); if (_fallbackCount > 1) Add(_fallback); }
                    _active = false; _completedTurn = completed; ClearFallback();
                    if (end > cutoff && !(reset && previouslyRead) && _count > 0 && Guid.TryParse(_id, out _))
                        candidate = new(_id, _project.Length > 0 ? _project + " | " + SessionNameFormatter.Shorten(_latest)
                            : _count == 1 ? SessionNameFormatter.Shorten(_first)
                            : SessionNameFormatter.Shorten(_first) + " | " + SessionNameFormatter.Shorten(_latest),
                            end, _project, _first, _latest);
                }
            }
            else if (type == "response_item" && kind == "message" && Get(payload, "role") == "user" && !_canonical)
            {
                var content = SessionBrowserStore.Property(payload, "content");
                if (content.ValueKind != JsonValueKind.Array) return;
                var text = string.Join("\n", content.EnumerateArray().Where(c => Get(c, "type") == "input_text").Select(c => Get(c, "text")));
                var clean = SessionNameFormatter.CleanUserText(text);
                if (clean.Length > 0)
                {
                    if (_fallbackCount == 0) _fallbackFirst = clean;
                    _fallback = clean; _fallbackCount = Math.Min(2, _fallbackCount + 1);
                }
            }
        }, () =>
        {
            reset = true; _id = _project = _first = _latest = _turn = _fallback = _completedTurn = _lastEvent = "";
            _count = 0; _internal = _active = _canonical = false;
            ClearFallback();
        }, token, bytes => bytes.Span.IndexOf("\"session_meta\""u8) >= 0 || bytes.Span.IndexOf("\"event_msg\""u8) >= 0 || bytes.Span.IndexOf("\"response_item\""u8) >= 0);
        return candidate;
    }

    private void Add(string text) { if (_count == 0) _first = text; _latest = text; _count = Math.Min(2, _count + 1); }
    private void ClearFallback() { _fallback = _fallbackFirst = ""; _fallbackCount = 0; }
    private static string ProjectName(string cwd)
    {
        var value = cwd.Trim().TrimEnd('\\', '/');
        if (value.Length == 0) return "";
        var separator = value.LastIndexOfAny(new[] { '\\', '/' });
        return separator < 0 ? value : value[(separator + 1)..];
    }
    private static string Get(JsonElement value, string key) => SessionBrowserStore.Text(SessionBrowserStore.Property(value, key));
}

using System.IO;
using System.Text.Json;

namespace CodexUsageAssistant.Services;

internal sealed record GoalLocalObservation(bool HasLifecycle, bool IsBusy, bool RequestSeen, string? TurnId, string? GoalStatus)
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Cursor> Cursors = new(StringComparer.Ordinal);
    private sealed class Cursor
    {
        internal IncrementalJsonlReader Reader = new(2 * 1024 * 1024);
        internal string? ActiveTurn, RequestTurn, Terminal;
        internal bool Lifecycle, Seen;
        internal HashSet<string> GoalCalls = [];
        internal void Reset() { ActiveTurn = RequestTurn = Terminal = null; Lifecycle = Seen = false; GoalCalls.Clear(); }
    }
    internal static void Forget(string path)
    {
        lock (Gate)
            foreach (var key in Cursors.Keys.Where(k => k.StartsWith(path + "\0", StringComparison.Ordinal)).ToArray()) Cursors.Remove(key);
    }
    internal static Task<GoalLocalObservation> ReadAsync(string path, string threadId, string? requestId, string? fingerprint, CancellationToken token) =>
        Task.Run(() => Read(path, threadId, requestId, fingerprint, token), token);

    private static GoalLocalObservation Read(string path, string threadId, string? requestId, string? fingerprint, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Contains(threadId, StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid rollout path.");
        lock (Gate)
        {
            var key = path + "\0" + requestId + "\0" + fingerprint;
            if (!Cursors.TryGetValue(key, out var state))
            {
                if (Cursors.Count >= 32) Cursors.Remove(Cursors.Keys.First());
                Cursors[key] = state = new();
            }
            state.Reader.Read(path, (root, _, _) =>
            {
                var payload = GoalProtocol.Property(root, "payload");
                var type = GoalProtocol.Text(payload, "type");
                if (GoalProtocol.Text(root, "type") == "event_msg")
                {
                    if (type == "task_started") { state.ActiveTurn = GoalProtocol.Text(payload, "turn_id"); state.Lifecycle = state.ActiveTurn is not null; }
                    if (type == "task_complete" && (state.ActiveTurn is null || GoalProtocol.Text(payload, "turn_id") == state.ActiveTurn))
                    { state.ActiveTurn = null; state.Lifecycle = true; }
                }
                if (type == "message" && GoalProtocol.Text(payload, "role") == "user" && requestId is not null)
                {
                    var content = GoalProtocol.Property(payload, "content");
                    if (content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(x =>
                        GoalProtocol.Text(x, "text")?.StartsWith($"[Codex EzMate {requestId}]\n", StringComparison.Ordinal) == true))
                    { state.Seen = true; state.RequestTurn = GoalProtocol.Text(GoalProtocol.Property(payload, "internal_chat_message_metadata_passthrough"), "turn_id"); }
                }
                if (type is "custom_tool_call" or "function_call")
                {
                    var input = GoalProtocol.Text(payload, "input") ?? "";
                    if (GoalProtocol.Text(payload, "name") == "update_goal" || input.Contains("tools.update_goal(", StringComparison.Ordinal))
                        if (GoalProtocol.Text(payload, "call_id") is { } call)
                        {
                            if (state.GoalCalls.Count >= 256) state.GoalCalls.Remove(state.GoalCalls.First());
                            state.GoalCalls.Add(call);
                        }
                }
                if (type is "custom_tool_call_output" or "function_call_output" && fingerprint is not null &&
                    GoalProtocol.Text(payload, "call_id") is { } outputCall && state.GoalCalls.Remove(outputCall))
                {
                    var output = GoalProtocol.Property(payload, "output");
                    IEnumerable<string?> texts = output.ValueKind == JsonValueKind.String ? [output.GetString()] :
                        output.ValueKind == JsonValueKind.Array ? output.EnumerateArray().Select(x => GoalProtocol.Text(x, "text")) : [];
                    foreach (var text in texts.Where(x => x is not null))
                        try
                        {
                            using var result = JsonDocument.Parse(text!); var goal = GoalProtocol.Property(result.RootElement, "goal");
                            if (GoalProtocol.Text(goal, "threadId") == threadId && GoalProtocol.GoalFingerprint(goal) == fingerprint)
                                state.Terminal = GoalProtocol.Text(goal, "status");
                        }
                        catch (JsonException) { }
                }
            }, state.Reset, token);
            return new(state.Lifecycle, state.ActiveTurn is not null, state.Seen, state.RequestTurn ?? state.ActiveTurn, state.Terminal);
        }
    }
}

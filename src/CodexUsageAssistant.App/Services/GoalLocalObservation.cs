using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexUsageAssistant.Services;

internal sealed record GoalLocalObservation(bool HasLifecycle, bool IsBusy, bool RequestSeen, string? TurnId, string? GoalStatus)
{
    internal static async Task<GoalLocalObservation> ReadAsync(string path, string threadId, string? requestId, string? fingerprint, CancellationToken token)
    {
        // Only read the rollout returned for this thread. Never write Codex state.
        if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Contains(threadId, StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid rollout path.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        var offset = Math.Max(0, file.Length - 2 * 1024 * 1024);
        file.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(file, Encoding.UTF8);
        if (offset > 0) await reader.ReadLineAsync(token).ConfigureAwait(false);
        string? activeTurn = null, requestTurn = null, terminal = null;
        var lifecycle = false; var seen = false;
        var goalCalls = new HashSet<string>();
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement; var payload = GoalProtocol.Property(root, "payload");
                var type = GoalProtocol.Text(payload, "type");
                if (GoalProtocol.Text(root, "type") == "event_msg")
                {
                    if (type == "task_started") { activeTurn = GoalProtocol.Text(payload, "turn_id"); lifecycle = activeTurn is not null; }
                    if (type == "task_complete" && (activeTurn is null || GoalProtocol.Text(payload, "turn_id") == activeTurn))
                    { activeTurn = null; lifecycle = true; }
                }
                if (type == "message" && GoalProtocol.Text(payload, "role") == "user" && requestId is not null)
                {
                    var content = GoalProtocol.Property(payload, "content");
                    if (content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(x =>
                        GoalProtocol.Text(x, "text")?.StartsWith($"[Codex EzMate {requestId}]\n", StringComparison.Ordinal) == true))
                    { seen = true; requestTurn = GoalProtocol.Text(GoalProtocol.Property(payload, "internal_chat_message_metadata_passthrough"), "turn_id"); }
                }
                if (type is "custom_tool_call" or "function_call")
                {
                    var input = GoalProtocol.Text(payload, "input") ?? "";
                    if (GoalProtocol.Text(payload, "name") == "update_goal" || input.Contains("tools.update_goal(", StringComparison.Ordinal))
                        if (GoalProtocol.Text(payload, "call_id") is { } call) goalCalls.Add(call);
                }
                if (type is "custom_tool_call_output" or "function_call_output" && fingerprint is not null &&
                    GoalProtocol.Text(payload, "call_id") is { } outputCall && goalCalls.Contains(outputCall))
                {
                    var output = GoalProtocol.Property(payload, "output");
                    IEnumerable<string?> texts = output.ValueKind == JsonValueKind.String ? [output.GetString()] :
                        output.ValueKind == JsonValueKind.Array ? output.EnumerateArray().Select(x => GoalProtocol.Text(x, "text")) : [];
                    foreach (var text in texts.Where(x => x is not null))
                        try
                        {
                            using var result = JsonDocument.Parse(text!); var goal = GoalProtocol.Property(result.RootElement, "goal");
                            if (GoalProtocol.Text(goal, "threadId") == threadId && GoalProtocol.GoalFingerprint(goal) == fingerprint)
                                terminal = GoalProtocol.Text(goal, "status");
                        }
                        catch (JsonException) { }
                }
            }
            catch (JsonException) { /* A concurrently appended line may be incomplete. */ }
        }
        return new(lifecycle, activeTurn is not null, seen, requestTurn ?? activeTurn, terminal);
    }
}

using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

internal static class GoalProtocol
{
    internal static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item : default;
    internal static string? Text(JsonElement value, string name) => Property(value, name).ValueKind == JsonValueKind.String ? Property(value, name).GetString() : null;
    internal static string? Status(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : Text(value, "type");
    internal static bool InternalSource(JsonElement source) => source.ValueKind == JsonValueKind.Object && source.EnumerateObject().Any(p => p.Name.Equals("subagent", StringComparison.OrdinalIgnoreCase)) ||
        source.ValueKind == JsonValueKind.String && source.GetString()!.StartsWith("subAgent", StringComparison.OrdinalIgnoreCase);
    internal static bool UsageAllowsResume(UsageData usage) => usage.Status == UsageStatus.Available && usage.DataSource == "App Server" &&
        usage.OrdinaryUsageAllowed == true && usage.FiveHourRemainingPercent is not <= 0 && usage.WeeklyRemainingPercent is not <= 0;
    internal static string GoalFingerprint(JsonElement goal) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes((Text(goal, "objective") ?? "") + "\n" + Property(goal, "createdAt").ToString())));
    internal static bool CanResumeGoal(string? status, bool manual) => status == "usageLimited" || manual && status == "paused";

    internal static IReadOnlyList<string> Decisions(string method, JsonElement parameters)
    {
        if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
        {
            var supplied = Property(parameters, "availableDecisions");
            return supplied.ValueKind == JsonValueKind.Array
                ? supplied.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && x.GetString() is "accept" or "decline" or "cancel").Select(x => x.GetString()!).ToArray()
                : ["accept", "decline", "cancel"];
        }
        if (method == "item/permissions/requestApproval" && Property(parameters, "permissions").ValueKind == JsonValueKind.Object)
            return ["accept", "decline"];
        if (method == "item/tool/requestUserInput" && Property(parameters, "questions").ValueKind == JsonValueKind.Array)
        {
            var questions = Property(parameters, "questions").EnumerateArray().ToArray();
            if (questions.Length is > 0 and <= 32 && questions.All(x => !string.IsNullOrWhiteSpace(Text(x, "id")) && Text(x, "question") is not null) &&
                questions.Select(x => Text(x, "id")).Distinct().Count() == questions.Length) return ["submit", "cancel"];
        }
        return [];
    }

    internal static object Response(GoalPendingRequest request, string decision, IReadOnlyDictionary<string, string[]>? answers)
    {
        if (!request.Decisions.Contains(decision)) throw new InvalidOperationException("Unsupported approval decision.");
        if (request.Method == "item/permissions/requestApproval")
            return new { permissions = decision == "accept" ? Property(request.Parameters, "permissions") : JsonSerializer.SerializeToElement(new { }), scope = "turn" };
        if (request.Method == "item/tool/requestUserInput")
        {
            var result = new Dictionary<string, object>();
            if (decision == "submit")
                foreach (var question in Property(request.Parameters, "questions").EnumerateArray())
                {
                    var id = Text(question, "id") ?? throw new InvalidDataException("Question ID missing.");
                    if (answers is null || !answers.TryGetValue(id, out var values) || values.Length == 0 || values.All(string.IsNullOrWhiteSpace))
                        throw new InvalidDataException("Answer all questions before submitting.");
                    result[id] = new { answers = values };
                }
            return new { answers = result };
        }
        return new { decision };
    }
}

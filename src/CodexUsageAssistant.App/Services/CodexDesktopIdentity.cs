namespace CodexUsageAssistant.Services;

internal static class CodexDesktopIdentity
{
    internal static bool Matches(string name, string? path) =>
        (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) || name.Equals("Codex", StringComparison.OrdinalIgnoreCase))
        && path is not null && (path.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\OpenAI\Codex\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Programs\Codex\", StringComparison.OrdinalIgnoreCase));
}

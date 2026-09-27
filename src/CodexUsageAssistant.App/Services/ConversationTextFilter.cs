using System.Text.RegularExpressions;

namespace CodexUsageAssistant.Services;

internal static class ConversationTextFilter
{
    internal static string Clean(string text)
    {
        text = text.Trim();
        if (text.StartsWith("[Codex EzMate ", StringComparison.Ordinal) ||
            text.StartsWith("The following is the Codex agent history", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("You are performing a CONTEXT CHECKPOINT", StringComparison.OrdinalIgnoreCase)) return "";
        if (text.StartsWith("# Files mentioned by the user:", StringComparison.Ordinal))
        {
            var request = text.IndexOf("## My request:", StringComparison.Ordinal);
            if (request < 0) return "";
            text = text[(request + "## My request:".Length)..].Trim();
        }
        // Remove known injected envelopes, not arbitrary user markup or code blocks.
        text = Regex.Replace(text, @"<(environment_context|recommended_plugins|skills_instructions|permissions instructions|developer_instructions|INSTRUCTIONS|subagent_notification|turn_aborted|send_user_message_question_reply)\b[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<image\b[^>]*>.*?</image>|<image\b[^>]*/>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
        if (text.StartsWith("# AGENTS.md instructions", StringComparison.OrdinalIgnoreCase)) return "";
        if (Regex.IsMatch(text, @"^<(environment_context|recommended_plugins|skills_instructions|INSTRUCTIONS|subagent_notification)\b", RegexOptions.IgnoreCase)) return "";
        return text;
    }
}

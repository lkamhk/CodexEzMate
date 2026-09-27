using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Services;
using Xunit;

namespace CodexUsageAssistant.Tests.Regression;

public sealed class ConversationPreviewTests
{
    [Theory]
    [InlineData("<environment_context>private</environment_context>\nHello", "Hello")]
    [InlineData("<recommended_plugins>private</recommended_plugins>", "")]
    [InlineData("# AGENTS.md instructions\n<INSTRUCTIONS>private</INSTRUCTIONS>", "")]
    [InlineData("[Codex EzMate abc]\nContinue", "")]
    [InlineData("# Files mentioned by the user:\nfile.png\n## My request:\nFix this", "Fix this")]
    [InlineData("```xml\n<sample>keep this</sample>\n```", "```xml\n<sample>keep this</sample>\n```")]
    public void CleansOnlyKnownInjectedContent(string text, string expected) => Assert.Equal(expected, ConversationTextFilter.Clean(text));

    [Fact]
    public void PreviewIncludesConversationButNotAnalysisOrToolMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), "EzMatePreview-" + Guid.NewGuid());
        try
        {
            var folder = Path.Combine(root, "sessions"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "rollout-01900000-0000-7000-8000-000000000099.jsonl");
            object Message(string role, string text, string channel = "final", string recipient = "all") => new
            { type = "response_item", payload = new { type = "message", role, channel, recipient, content = new[] { new { type = "input_text", text } } } };
            var records = new[] { Message("system", "secret system"), Message("user", "<environment_context>private</environment_context>"),
                Message("user", "Real question"), Message("assistant", "private reasoning", "analysis"),
                Message("assistant", "tool call", "commentary", "functions.exec"), Message("assistant", "Real answer") };
            File.WriteAllText(path, string.Join("\n", records.Select(r => JsonSerializer.Serialize(r))));
            var store = new SessionBrowserStore(root, root); var session = Assert.Single(store.Scan(default).Sessions);
            var page = store.ReadPreviewPage(session, null, default);
            Assert.Equal(new[] { "Real question", "Real answer" }, page.Parts.Select(p => p.Text));
            Assert.Equal(new[] { "user", "assistant" }, page.Parts.Select(p => p.Role));
            Assert.DoesNotContain("private", page.Text); Assert.DoesNotContain("tool call", page.Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

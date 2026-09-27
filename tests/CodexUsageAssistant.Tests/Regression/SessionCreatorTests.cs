using CodexUsageAssistant.Models;
using Xunit;

namespace CodexUsageAssistant.Tests.Regression;

public sealed class SessionCreatorTests
{
    [Theory]
    [InlineData("Codex Desktop", "vscode", "Codex")]
    [InlineData("codex_vscode", "vscode", "VS Code")]
    [InlineData("codex_cli_rs", "cli", "CLI")]
    [InlineData("", "cli", "CLI")]
    [InlineData("", "vscode", "Unknown")]
    [InlineData("", "", "Unknown")]
    public void CreatorUsesOriginatorToDisambiguateDesktop(string originator, string source, string expected)
    {
        var session = new BrowserSession { Originator = originator, Source = source };
        session.SetLanguage(AppLanguage.English);
        Assert.Equal(expected, session.CreatorDisplay);
    }
}

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CodexUsageAssistant.Models;

public enum NamingSegmentSource { ProjectName, FirstPrompt, LatestPrompt, CustomText, SessionId }

public sealed class NamingSegment
{
    public NamingSegmentSource Source { get; set; } = NamingSegmentSource.LatestPrompt;
    public int MaxCharacters { get; set; } = 30;
    public string Text { get; set; } = "";
    public NamingSegment Copy() => new() { Source = Source, MaxCharacters = MaxCharacters, Text = Text };
}

public sealed class SessionNamingRules
{
    public string Separator { get; set; } = " | ";
    public List<NamingSegment> Segments { get; set; } =
        [new() { Source = NamingSegmentSource.ProjectName, MaxCharacters = 80 }, new() { MaxCharacters = 5 }];

    public SessionNamingRules Copy() => new() { Separator = Separator, Segments = Segments.Select(s => s.Copy()).ToList() };

    public void Validate()
    {
        if (Separator is null || Separator.Length > 16 || Separator.Any(char.IsControl))
            throw new InvalidDataException("分隔符最多 16 字元，不可包含換行。 / Separator: up to 16 characters, no line breaks.");
        if (Segments is null || Segments.Count is < 1 or > 8 || Segments.Any(s => s is null || !Enum.IsDefined(s.Source)
            || s.MaxCharacters is < 1 or > 100 || s.Text is null || s.Text.Length > 1000 || s.Text.Any(char.IsControl)))
            throw new InvalidDataException("請設定 1–8 段，每段字數 1–100；固定文字不可包含換行。 / Use 1–8 segments, 1–100 characters each, without line breaks.");
        if (Segments.Sum(s => s.MaxCharacters) + new StringInfo(Separator).LengthInTextElements * (Segments.Count - 1) > 240)
            throw new InvalidDataException("所有命名段及分隔符的字數上限合計不可超過 240。 / Combined character limits must not exceed 240.");
        if (Segments.Any(s => s.Source == NamingSegmentSource.CustomText && string.IsNullOrWhiteSpace(s.Text)))
            throw new InvalidDataException("固定文字不可留空。 / Custom text cannot be empty.");
    }

    public string Format(string project, string first, string latest, string id)
    {
        Validate();
        return string.Join(Separator, Segments.Select(s => Limit(s.Source switch
        {
            NamingSegmentSource.ProjectName => project,
            NamingSegmentSource.FirstPrompt => first,
            NamingSegmentSource.LatestPrompt => latest,
            NamingSegmentSource.CustomText => s.Text,
            NamingSegmentSource.SessionId => id,
            _ => ""
        }, s.MaxCharacters)).Where(s => s.Length > 0));
    }

    internal static string Limit(string text, int maximum)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var offsets = StringInfo.ParseCombiningCharacters(text);
        return offsets.Length <= maximum ? text : maximum == 1 ? "…" : text[..offsets[maximum - 1]] + "…";
    }
}

public sealed record SessionNamingConfiguration(bool Enabled, SessionNamingRules Rules, IReadOnlyList<string> ExcludedIds, bool UsesLegacyRules);

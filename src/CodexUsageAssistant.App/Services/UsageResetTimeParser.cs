using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexUsageAssistant.Services;

public static class UsageResetTimeParser
{
    private static readonly CultureInfo[] Cultures =
    [
        CultureInfo.CurrentCulture,
        CultureInfo.GetCultureInfo("en-US"),
        CultureInfo.GetCultureInfo("zh-HK"),
        CultureInfo.GetCultureInfo("zh-TW")
    ];

    public static DateTimeOffset? Parse(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = Regex.Replace(text.Trim(), @"^(?:resets?|重置)\s*[:：]?\s*", string.Empty, RegexOptions.IgnoreCase);
        value = value.Replace("上午", "AM ", StringComparison.Ordinal)
            .Replace("下午", "PM ", StringComparison.Ordinal);

        var isTimeOnly = Regex.IsMatch(value, @"^(?:AM\s+|PM\s+)?\d{1,2}:\d{2}(?:\s*(?:AM|PM))?$", RegexOptions.IgnoreCase);
        foreach (var culture in Cultures.DistinctBy(culture => culture.Name))
        {
            if (!DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) continue;
            if (isTimeOnly)
            {
                var localCandidate = now.Date.Add(parsed.TimeOfDay);
                var candidate = new DateTimeOffset(localCandidate, now.Offset);
                return candidate <= now ? candidate.AddDays(1) : candidate;
            }

            var offset = TimeZoneInfo.Local.GetUtcOffset(parsed);
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), offset);
        }
        return null;
    }

    public static DateTimeOffset? ParseExpiration(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = Regex.Replace(text.Trim(), @"^(?:expires?(?:\s+on)?|valid\s+until|available\s+until|有效期(?:至)?|到期(?:日)?)\s*[:：]?\s*", string.Empty,
            RegexOptions.IgnoreCase);
        if (Regex.IsMatch(value, @"^[A-Za-z]", RegexOptions.CultureInvariant))
            return ParseEnglishExpiration(value, now);
        // An expiration must contain a date; time-only parsing silently inserts today's date.
        if (Regex.IsMatch(value, @"^(?:上午|下午|AM|PM)?\s*\d{1,2}:\d{2}(?::\d{2})?(?:\s*(?:AM|PM))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return null;
        value = value.Replace("上午", "AM ", StringComparison.Ordinal)
            .Replace("下午", "PM ", StringComparison.Ordinal);
        var hasYear = Regex.IsMatch(value, @"\b\d{4}\b|\d{4}\s*年", RegexOptions.CultureInvariant);

        foreach (var culture in Cultures.DistinctBy(culture => culture.Name))
        {
            if (!DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) continue;
            if (!hasYear)
            {
                parsed = new DateTime(now.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, parsed.Second);
                if (parsed.Date < now.Date) parsed = parsed.AddYears(1);
            }

            var offset = TimeZoneInfo.Local.GetUtcOffset(parsed);
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), offset);
        }
        return null;
    }

    private static DateTimeOffset? ParseEnglishExpiration(string text, DateTimeOffset now)
    {
        var value = Regex.Replace(text.Replace(',', ' '), @"\s+", " ").Trim();
        var parts = Regex.Match(value,
            @"^(?<month>[A-Za-z]+)\s+(?<day>\d{1,2})(?:\s+(?<year>\d{4}))?(?:\s+(?<time>\d{1,2}:\d{2}(?::\d{2})?(?:\s*[AP]M)?))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!parts.Success) return null;
        var hasYear = parts.Groups["year"].Success;
        var year = hasYear ? parts.Groups["year"].Value : now.Year.ToString(CultureInfo.InvariantCulture);
        var time = Regex.Replace(parts.Groups["time"].Value, @"\s*([AP]M)$", " $1", RegexOptions.IgnoreCase).ToUpperInvariant();
        var datedValue = $"{year} {parts.Groups["month"].Value} {parts.Groups["day"].Value} {time}".Trim();
        string[] formats =
        [
            "yyyy MMM d", "yyyy MMMM d",
            "yyyy MMM d h:mm tt", "yyyy MMMM d h:mm tt",
            "yyyy MMM d h:mm:ss tt", "yyyy MMMM d h:mm:ss tt",
            "yyyy MMM d H:mm", "yyyy MMMM d H:mm",
            "yyyy MMM d H:mm:ss", "yyyy MMMM d H:mm:ss"
        ];
        if (!DateTime.TryParseExact(datedValue, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed)) return null;
        if (!hasYear && parsed.Date < now.Date) parsed = parsed.AddYears(1);
        return new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
    }
}

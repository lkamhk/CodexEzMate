using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

internal static class CodexResetsApiAdapter
{
    internal const string FeedId = "codex-resets-api-v1";
    internal const string SourceId = "codex-resets-com";
    internal static bool Matches(Uri address) => address.Scheme == "https" && address.IsDefaultPort
        && address.Host.Equals("codex-resets.com", StringComparison.OrdinalIgnoreCase)
        && address.AbsolutePath is "/api/v1/status" or "/api/v1/resets";

    internal static ResetFeed Parse(JsonElement root, DateTimeOffset now)
    {
        var meta = Property(root, "meta");
        if (Text(meta, "api_version") != "v1") throw new InvalidDataException("Unsupported Codex Resets API version.");
        var generated = Time(meta, "generated_at");
        var data = Property(root, "data"); var events = new List<ResetAnnouncement>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            if (data.GetArrayLength() > 100) throw new InvalidDataException("Too many API events.");
            foreach (var item in data.EnumerateArray()) events.Add(Reset(item, false));
        }
        else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("latest_reset", out var latest)
            && data.TryGetProperty("scheduled_reset", out var scheduled) && data.TryGetProperty("active_watch", out var watch))
        {
            if (latest.ValueKind != JsonValueKind.Null) events.Add(Reset(latest, false));
            if (scheduled.ValueKind != JsonValueKind.Null)
            {
                if (Text(scheduled, "status") != "scheduled") throw new InvalidDataException("Invalid scheduled reset.");
                var item = Reset(scheduled, true);
                if (events.All(x => x.Id != item.Id)) events.Add(item);
            }
            if (watch.ValueKind != JsonValueKind.Null)
            {
                var level = Text(watch, "level");
                if (level is not ("elevated" or "strong")) throw new InvalidDataException("Invalid forecast level.");
                var observed = Time(watch, "observed_at"); var expires = Time(watch, "expires_at");
                if (expires < observed) throw new InvalidDataException("Invalid forecast window.");
                var probability = Property(watch, "reset_chance_percent");
                if (probability.ValueKind != JsonValueKind.Null && (probability.ValueKind != JsonValueKind.Number || !probability.TryGetInt32(out var chance) || chance is < 0 or > 100))
                    throw new InvalidDataException("Invalid forecast probability.");
                var source = Property(watch, "source");
                var key = Text(source, "url"); if (key.Length == 0) key = observed.ToString("O");
                events.Add(new()
                {
                    Id = "watch-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24], Revision = 1,
                    Title = "Codex Reset — AI forecast / AI 分類預測",
                    Summary = Clip(Text(watch, "text"), 4000), Status = ResetAnnouncementStatus.Preview,
                    Evidence = ResetEvidence.Tracker, Impacts = [ResetImpact.Global], PublishedAt = observed, UpdatedAt = observed,
                    ExpectedEndAt = expires, TimeDescription = Clip(Text(watch, "forecast_window"), 700),
                    Scope = "Codex Resets AI-classified forecast; not an OpenAI commitment. / 網站 AI 分類預測，並非 OpenAI 承諾。",
                    Links = Links(source)
                });
            }
        }
        else throw new InvalidDataException("Invalid Codex Resets API response.");
        var feed = new ResetFeed
        {
            SchemaVersion = 1, FeedId = FeedId, GeneratedAt = generated,
            Sources = [new() { Id = SourceId, Name = "Codex Resets · codex-resets.com", Health = ResetSourceHealth.Healthy,
                LastSuccessfulCheckAt = generated, Message = "Public tracker API; not affiliated with OpenAI. / 第三方追蹤網站 API，非 OpenAI 官方。" }],
            Events = events.ToArray()
        };
        ResetFeedClient.Validate(feed, now); return feed;
    }

    private static ResetAnnouncement Reset(JsonElement item, bool scheduled)
    {
        var id = Text(item, "id");
        if (!ResetFeedClient.ValidId(id) || id.Length > 64) throw new InvalidDataException("Invalid reset ID.");
        var type = Text(item, "reset_type");
        if (type is not ("regular" or "banked")) throw new InvalidDataException("Invalid reset type.");
        var announced = Time(item, "announced_at");
        return new()
        {
            Id = "reset-" + id, Revision = 1, Title = scheduled ? "Codex " + type + " reset — scheduled / 已排程" : "Codex " + type + " reset",
            Summary = Clip(Text(item, "text"), 4000), Status = scheduled ? ResetAnnouncementStatus.Preview : ResetAnnouncementStatus.Announced,
            Evidence = ResetEvidence.Tracker, Impacts = [type == "banked" ? ResetImpact.Banked : ResetImpact.Global],
            PublishedAt = announced, UpdatedAt = announced,
            ExpectedStartAt = scheduled && Property(item, "scheduled_for").ValueKind != JsonValueKind.Null ? Time(item, "scheduled_for") : null,
            Scope = scheduled ? "Awaiting execution evidence; a passed schedule is not completion. / 等待執行證據，超過排程時間不代表已發放。"
                : "Reported by Codex Resets; account receipt is not confirmed. / 網站彙整消息，不代表個別帳戶已到帳。",
            Links = Links(Property(item, "source"))
        };
    }

    private static ResetAnnouncementLink[] Links(JsonElement source)
    {
        var type = Text(source, "type");
        if (type is not ("x_post" or "observed")) throw new InvalidDataException("Invalid reset source.");
        var links = new List<ResetAnnouncementLink> { new() { SourceId = SourceId, Label = "Data from Codex Resets", Url = "https://codex-resets.com" } };
        var url = Text(source, "url");
        if (type == "x_post" && (Text(source, "author") != "thsottiaux" || url.Length == 0)) throw new InvalidDataException("Invalid post source.");
        if (url.Length > 0)
        {
            if (!ResetFeedClient.TryGetHttpsUri(url, out _)) throw new InvalidDataException("Invalid source URL.");
            links.Add(new() { SourceId = SourceId, Label = "Source / 原文", Url = url });
        }
        return links.ToArray();
    }

    internal static ResetFeed Reconcile(ResetFeed feed, ResetWatchState previous)
    {
        if (previous.Feed?.FeedId != FeedId) return feed;
        var events = feed.Events.Select(item =>
        {
            var old = previous.Feed.Events.FirstOrDefault(x => x.Id == item.Id);
            if (old is null) return item with { Revision = previous.Seen.GetValueOrDefault(item.Id)?.Revision ?? 1 };
            static string Content(ResetAnnouncement value) => JsonSerializer.Serialize(value with { Revision = 1, UpdatedAt = value.PublishedAt }, ResetFeedClient.JsonOptions);
            if (Content(old) == Content(item)) return old;
            if (old.Revision == int.MaxValue) throw new InvalidDataException("Revision limit reached.");
            return item with { Revision = old.Revision + 1, UpdatedAt = feed.GeneratedAt };
        }).ToArray();
        return feed with { Events = events };
    }

    internal static ResetFeed Revalidated(ResetFeed feed, DateTimeOffset now) => feed with
    { GeneratedAt = now, Sources = feed.Sources.Select(s => s with { LastSuccessfulCheckAt = now }).ToArray() };
    private static JsonElement Property(JsonElement item, string name) => SessionBrowserStore.Property(item, name);
    private static string Text(JsonElement item, string name) => SessionBrowserStore.Text(Property(item, name));
    private static DateTimeOffset Time(JsonElement item, string name)
    {
        var value = Property(item, name);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Missing API timestamp.");
        return value.Deserialize<DateTimeOffset>(ResetFeedClient.JsonOptions);
    }
    private static string Clip(string value, int maximum)
    {
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value.Length <= maximum ? value : value[..(char.IsHighSurrogate(value[maximum - 2]) ? maximum - 2 : maximum - 1)] + "…";
    }
}

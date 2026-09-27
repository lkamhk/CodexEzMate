using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed record ResetFeedResponse(ResetFeed? Feed, string? ETag = null,
    DateTimeOffset? LastModified = null, bool NotModified = false);

public interface IResetFeedClient
{
    Task<ResetFeedResponse> FetchAsync(Uri address, string? etag, DateTimeOffset? lastModified, CancellationToken token);
}

public sealed class ResetFeedClient : IResetFeedClient, IDisposable
{
    public const int MaximumBytes = 512 * 1024;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(1);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 20,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false), new FeedTimestampConverter() }
    };
    private readonly HttpClient _http;

    public ResetFeedClient() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { }

    public ResetFeedClient(HttpMessageHandler handler) =>
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public static bool TryGetHttpsUri(string? value, out Uri address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        address = uri;
        return true;
    }

    public async Task<ResetFeedResponse> FetchAsync(Uri address, string? etag, DateTimeOffset? lastModified, CancellationToken token)
    {
        if (!TryGetHttpsUri(address.AbsoluteUri, out _)) throw new InvalidDataException("An HTTPS feed URL is required.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (etag?.Length <= 1024 && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
        if (lastModified.HasValue) request.Headers.IfModifiedSince = lastModified;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new(null, response.Headers.ETag?.ToString() ?? etag, response.Content.Headers.LastModified ?? lastModified, true);
        // No automatic redirects, account authentication, cookies, scripts or HTML rendering.
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Feed is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var data = await ReadBoundedAsync(stream, MaximumBytes, timeout.Token).ConfigureAwait(false);
        return new(Parse(data, DateTimeOffset.UtcNow), response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > maximum) throw new InvalidDataException("JSON is too large.");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }

    public static ResetFeed Parse(byte[] data, DateTimeOffset now)
    {
        if (data.Length > MaximumBytes) throw new InvalidDataException("Feed is too large.");
        using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 20 });
        RejectDuplicateProperties(document.RootElement);
        var feed = document.RootElement.Deserialize<ResetFeed>(JsonOptions) ?? throw new InvalidDataException("Empty feed.");
        Validate(feed, now);
        return feed;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    internal static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    internal static void Validate(ResetFeed feed, DateTimeOffset now)
    {
        static bool Text(string? value, int max, bool required = false) => value is not null && value.Length <= max
            && (!required || !string.IsNullOrWhiteSpace(value))
            && !value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t');
        static void Require(bool valid) { if (!valid) throw new InvalidDataException("Invalid Reset feed schema or data."); }
        Require(feed.SchemaVersion == 1 && ValidId(feed.FeedId) && feed.GeneratedAt != default && feed.GeneratedAt <= now.AddMinutes(5));
        Require(feed.Events is { Length: <= 200 } && feed.Sources is { Length: > 0 and <= 32 });
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in feed.Sources!)
        {
            Require(source is not null && ValidId(source.Id) && Text(source.Name, 120, true)
                && Text(source.Message, 1000) && Enum.IsDefined(source.Health));
            Require(sourceIds.Add(source!.Id));
            Require(source.LastSuccessfulCheckAt is null || source.LastSuccessfulCheckAt != default(DateTimeOffset)
                && source.LastSuccessfulCheckAt <= feed.GeneratedAt.AddMinutes(5) && source.LastSuccessfulCheckAt <= now.AddMinutes(5));
            Require(source.Health != ResetSourceHealth.Healthy || source.LastSuccessfulCheckAt.HasValue);
        }
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in feed.Events!)
        {
            Require(item is not null && ValidId(item.Id) && item.Revision > 0 && Text(item.Title, 240, true)
                && !item.Title.Any(char.IsControl) && Text(item.Summary, 4000) && Text(item.Scope, 1000)
                && Text(item.TimeDescription, 1000) && Enum.IsDefined(item.Status) && Enum.IsDefined(item.Evidence)
                && item.Impacts is { Length: > 0 and <= 3 } && item.Impacts.All(Enum.IsDefined)
                && item.Impacts.Distinct().Count() == item.Impacts.Length
                && item.Links is { Length: > 0 and <= 10 } && item.PublishedAt != default
                && item.UpdatedAt >= item.PublishedAt && item.UpdatedAt <= feed.GeneratedAt.AddMinutes(5) && item.UpdatedAt <= now.AddMinutes(5)
                && (item.RelatedEventId is null || ValidId(item.RelatedEventId) && item.RelatedEventId != item.Id)
                && (item.ExpectedStartAt is null || item.ExpectedEndAt is null || item.ExpectedStartAt <= item.ExpectedEndAt));
            Require(eventIds.Add(item!.Id));
            foreach (var link in item.Links)
                Require(link is not null && sourceIds.Contains(link.SourceId) && Text(link.Label, 160, true) && TryGetHttpsUri(link.Url, out _));
        }
    }

    public static ResetFeed ReadSample()
    {
        using var stream = typeof(ResetFeedClient).Assembly.GetManifestResourceStream("CodexUsageAssistant.ResetFeed.Sample.json")
            ?? throw new InvalidDataException("Sample feed is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray(), DateTimeOffset.UtcNow);
    }

    public void Dispose() => _http.Dispose();

    private sealed class FeedTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("An ISO 8601 timestamp is required.");
            var text = reader.GetString()!;
            var explicitZone = text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':';
            if (!explicitZone || !reader.TryGetDateTimeOffset(out var timestamp))
                throw new JsonException("Feed timestamps must include Z or an explicit UTC offset.");
            return timestamp;
        }
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}

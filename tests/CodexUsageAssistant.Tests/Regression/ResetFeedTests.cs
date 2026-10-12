using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Xunit;
using static CodexUsageAssistant.Tests.Regression.ResetWatchTestData;

namespace CodexUsageAssistant.Tests.Regression;

public sealed class ResetFeedTests
{
    [Fact]
    public void ValidFeedAndEmbeddedSampleRoundTrip()
    {
        var input = Feed(Event());
        Assert.Equal(Bytes(input), Bytes(ResetFeedClient.Parse(Bytes(input), Now)));
        var sample = ResetFeedClient.ReadSample();
        Assert.Equal(3, sample.Events.Length);
        Assert.All(sample.Events, x => Assert.StartsWith("[DEMO", x.Title));
    }

    [Theory]
    [InlineData("version")][InlineData("missingEvidence")][InlineData("missingStatus")]
    [InlineData("unknownEnum")][InlineData("numericEnum")][InlineData("nullEvents")]
    [InlineData("nullEvent")][InlineData("nullSource")][InlineData("nullTitle")]
    [InlineData("nullLinks")][InlineData("nullLink")][InlineData("unknownSource")]
    [InlineData("duplicateEvent")][InlineData("duplicateSource")][InlineData("duplicateImpact")]
    [InlineData("futureFeed")][InlineData("backwardsTime")][InlineData("backwardsRange")]
    [InlineData("unsafeLink")][InlineData("healthyWithoutTimestamp")][InlineData("largeTitle")]
    [InlineData("missingTimezone")][InlineData("stringRevision")]
    public void RejectsMalformedFeedWithoutCoercingIt(string kind)
    {
        var node = JsonNode.Parse(Bytes(Feed(Event())))!;
        var item = node["events"]![0]!;
        switch (kind)
        {
            case "version": node["schemaVersion"] = 99; break;
            case "missingEvidence": item.AsObject().Remove("evidence"); break;
            case "missingStatus": item.AsObject().Remove("status"); break;
            case "unknownEnum": item["evidence"] = "madeUp"; break;
            case "numericEnum": item["evidence"] = 0; break;
            case "nullEvents": node["events"] = null; break;
            case "nullEvent": node["events"]![0] = null; break;
            case "nullSource": node["sources"]![0] = null; break;
            case "nullTitle": item["title"] = null; break;
            case "nullLinks": item["links"] = null; break;
            case "nullLink": item["links"]![0] = null; break;
            case "unknownSource": item["links"]![0]!["sourceId"] = "missing"; break;
            case "duplicateEvent": node["events"]!.AsArray().Add(item.DeepClone()); break;
            case "duplicateSource": node["sources"]!.AsArray().Add(node["sources"]![0]!.DeepClone()); break;
            case "duplicateImpact": item["impacts"]!.AsArray().Add("banked"); break;
            case "futureFeed": node["generatedAt"] = Now.AddDays(1); break;
            case "backwardsTime": item["updatedAt"] = Now.AddDays(-1); break;
            case "backwardsRange": item["expectedStartAt"] = Now.AddHours(1); item["expectedEndAt"] = Now; break;
            case "unsafeLink": item["links"]![0]!["url"] = "file:///C:/Windows/notepad.exe"; break;
            case "healthyWithoutTimestamp": node["sources"]![0]!["lastSuccessfulCheckAt"] = null; break;
            case "largeTitle": item["title"] = new string('x', 241); break;
            case "missingTimezone": item["publishedAt"] = "2025-06-01T11:50:00"; break;
            case "stringRevision": item["revision"] = "1"; break;
        }
        var error = Record.Exception(() => ResetFeedClient.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), Now));
        Assert.True(error is JsonException or InvalidDataException, error?.ToString() ?? "Invalid feed was accepted");
    }

    [Fact]
    public void DuplicateJsonKeysAndExcessiveEventsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ResetFeedClient.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1}"), Now));
        Assert.Throws<InvalidDataException>(() => ResetFeedClient.Parse(Bytes(Feed(Enumerable.Range(0, 201).Select(x => Event("e-" + x)).ToArray())), Now));
    }

    [Theory]
    [InlineData("http://example.com/feed.json")][InlineData("file:///C:/file.json")]
    [InlineData("javascript:alert(1)")][InlineData("https://user:pass@example.com/feed.json")]
    [InlineData("https://example.com/feed.json#section")][InlineData("https://example.com/\r\nheader")]
    [InlineData("")][InlineData(null)]
    public void UnsafeUrlsAreRejected(string? url) => Assert.False(ResetFeedClient.TryGetHttpsUri(url, out _));

    [Fact]
    public async Task ConditionalFetchSendsNoAccountData()
    {
        using var client = new ResetFeedClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("\"v1\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            Assert.Equal(Now, request.Headers.IfModifiedSince);
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(Feed(Event()))) };
            result.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            return result;
        }));
        var response = await client.FetchAsync(new Uri(Settings.FeedUrl), "\"v1\"", Now, CancellationToken.None);
        Assert.Equal("\"v2\"", response.ETag);
        Assert.Single(response.Feed!.Events);
    }

    [Fact]
    public async Task Http304RetainsValidators()
    {
        using var client = new ResetFeedClient(new Handler(_ => new(HttpStatusCode.NotModified)));
        var response = await client.FetchAsync(new Uri(Settings.FeedUrl), "\"v1\"", Now, CancellationToken.None);
        Assert.True(response.NotModified);
        Assert.Null(response.Feed);
        Assert.Equal("\"v1\"", response.ETag);
        Assert.Equal(Now, response.LastModified);
    }

    [Theory]
    [InlineData(301)][InlineData(404)][InlineData(429)][InlineData(500)]
    public async Task RedirectsAndFailuresAreNotParsed(int status)
    {
        using var client = new ResetFeedClient(new Handler(_ => new((HttpStatusCode)status)));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.FetchAsync(new Uri(Settings.FeedUrl), null, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task BodyLimitAppliesWithOrWithoutContentLength(bool lengthKnown)
    {
        using var client = new ResetFeedClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = lengthKnown ? new ByteArrayContent(new byte[ResetFeedClient.MaximumBytes + 1])
                : new StreamContent(new UnseekableStream(new byte[ResetFeedClient.MaximumBytes + 1]))
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(new Uri(Settings.FeedUrl), null, null, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsFetch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new ResetFeedClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(Feed())) }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(new Uri(Settings.FeedUrl), null, null, cancellation.Token));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); }
    }
    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    { public override bool CanSeek => false; }
}

using System.IO;
using System.Text;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed partial class SessionBrowserStore
{
    private readonly object _scanGate = new(), _previewGate = new();
    private sealed record Head(long Length, DateTime Written, DateTime Created, BrowserSession Session, long Bytes);
    private long _headBytes;
    private readonly Dictionary<(string, bool, bool, bool), Head> _heads = [];
    private sealed record MessageRange(string Path, long Start, long End, bool Event);
    private sealed record PreviewIndex(string Identity, long Length, long WrittenTicks, List<MessageRange> Messages, bool Truncated);
    private PreviewIndex? _index;
    private readonly Dictionary<(int, int), BrowserPreviewPage> _pages = [];
    public const int PreviewPageCharacters = 64 * 1024;

    public void ClearCaches()
    {
        lock (_scanGate) { _heads.Clear(); _headBytes = 0; }
        ClearPreviewCache();
    }
    public void ClearPreviewCache() { lock (_previewGate) { _index = null; _pages.Clear(); } }

    public BrowserPreviewPage ReadPreviewPage(BrowserSession session, BrowserPreviewCursor? cursor, CancellationToken token)
    {
        lock (_previewGate)
        {
            token.ThrowIfCancellationRequested(); ValidateSource(session);
            var signature = PreviewSignature(session);
            var invalidated = cursor is not null && (cursor.Path != signature.Identity || cursor.Length != signature.Length || cursor.WrittenTicks != signature.WrittenTicks);
            if (_index is null || _index.Identity != signature.Identity || _index.Length != signature.Length || _index.WrittenTicks != signature.WrittenTicks)
            {
                _index = BuildIndex(session, token); _pages.Clear();
            }
            if (invalidated) cursor = null;
            var messageIndex = cursor?.Message ?? 0; var character = cursor?.Character ?? 0;
            if (messageIndex < 0 || messageIndex > _index.Messages.Count || character < 0) throw new InvalidDataException("Invalid preview cursor.");
            var key = (messageIndex, character);
            if (_pages.TryGetValue(key, out var page)) return page with { Reloaded = invalidated };
            var text = new StringBuilder(PreviewPageCharacters);
            var parts = new List<BrowserPreviewPart>();
            while (messageIndex < _index.Messages.Count && text.Length < PreviewPageCharacters)
            {
                token.ThrowIfCancellationRequested();
                var range = _index.Messages[messageIndex];
                if (range.End - range.Start > IncrementalJsonlReader.MaxLineBytes) throw new InvalidDataException("Preview record is too large.");
                using var stream = new FileStream(range.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Position = range.Start;
                var bytes = new byte[(int)(range.End - range.Start)]; stream.ReadExactly(bytes);
                using var json = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0));
                var message = range.Event ? new BrowserMessage(Get(json.RootElement, "timestamp"), "user", EventText(json.RootElement)) : Response(json.RootElement);
                var formatted = message is null ? "" : $"{message.Role.ToUpperInvariant()}  {message.Timestamp}\n{message.Text}\n\n";
                if (character > formatted.Length) throw new InvalidDataException("Preview cursor no longer matches the message.");
                var length = Math.Min(PreviewPageCharacters - text.Length, formatted.Length - character);
                if (length > 0 && character + length < formatted.Length && char.IsHighSurrogate(formatted[character + length - 1])) length--;
                if (length == 0 && character < formatted.Length) break;
                if (message is not null)
                {
                    var header = $"{message.Role.ToUpperInvariant()}  {message.Timestamp}\n".Length;
                    var bodyStart = Math.Max(character, header);
                    var bodyEnd = Math.Min(character + length, header + message.Text.Length);
                    if (bodyEnd > bodyStart) parts.Add(new(message.Role, message.Timestamp, formatted.Substring(bodyStart, bodyEnd - bodyStart), character > header));
                }
                text.Append(formatted, character, length); character += length;
                if (character == formatted.Length) { messageIndex++; character = 0; }
            }
            BrowserPreviewCursor? next = messageIndex < _index.Messages.Count ? new(_index.Identity, _index.Length, _index.WrittenTicks, messageIndex, character) : null;
            var after = PreviewSignature(session);
            if (after.Identity != _index.Identity || after.Length != _index.Length || after.WrittenTicks != _index.WrittenTicks)
            { _index = null; _pages.Clear(); throw new IOException("Conversation changed while reading; reload the preview."); }
            page = new(text.ToString(), next, invalidated, _index.Truncated) { Parts = parts };
            if (_pages.Count >= 3) _pages.Remove(_pages.Keys.First());
            _pages[key] = page with { Reloaded = false };
            return page;
        }
    }

    private PreviewIndex BuildIndex(BrowserSession session, CancellationToken token)
    {
        var responses = new List<MessageRange>(); var events = new List<MessageRange>();
        var capped = false; long characters = 0;
        foreach (var part in EffectiveParts(session))
        {
            var reader = new IncrementalJsonlReader();
            reader.Read(part.Path, (record, from, to) =>
            {
                // Index offsets only; no conversation bodies survive indexing.
                if (characters >= 32 * 1024 * 1024) { capped = true; return; }
                characters += reader.RecordCharacters + 1;
                if (responses.Count >= 1000) { capped = true; return; }
                if (GoalProtocol.Text(record, "type") == "response_item")
                {
                    if (Response(record) is not null) responses.Add(new(part.Path, from, to, false));
                }
                else if (events.Count < 1000 && EventText(record).Length > 0) events.Add(new(part.Path, from, to, true));
            }, () => { }, token, maxBytes: Math.Min(part.MaxReadLength ?? long.MaxValue, 128L * 1024 * 1024));
            if (capped || responses.Count >= 1000) break;
        }
        var signature = PreviewSignature(session);
        return new(signature.Identity, signature.Length, signature.WrittenTicks, responses.Count > 0 ? responses : events,
            capped || EffectiveParts(session).Any(part => (part.MaxReadLength ?? part.Size) > 128L * 1024 * 1024));
    }

    private static IReadOnlyList<BrowserSessionPart> EffectiveParts(BrowserSession session) =>
        session.Parts.Count > 0 ? session.Parts : [new(session.Path, new FileInfo(session.Path).Length, File.GetLastWriteTime(session.Path))];

    private static (string Identity, long Length, long WrittenTicks) PreviewSignature(BrowserSession session)
    {
        var parts = EffectiveParts(session);
        var identity = string.Join("|", parts.Select(part => part.Path));
        long length = 0, written = 0;
        foreach (var part in parts)
        {
            var info = new FileInfo(part.Path);
            length += Math.Min(info.Length, part.MaxReadLength ?? info.Length);
            written = Math.Max(written, info.LastWriteTimeUtc.Ticks);
        }
        return (identity, length, written);
    }
}

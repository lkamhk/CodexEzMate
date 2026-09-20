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
    private sealed record MessageRange(long Start, long End, bool Event);
    private sealed record PreviewIndex(string Path, long Length, DateTime Written, DateTime Created, List<MessageRange> Messages, bool Truncated);
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
            var info = new FileInfo(session.Path);
            var invalidated = cursor is not null && (cursor.Path != session.Path || cursor.Length != info.Length || cursor.WrittenTicks != info.LastWriteTimeUtc.Ticks);
            if (_index is null || _index.Path != session.Path || _index.Length != info.Length || _index.Written != info.LastWriteTimeUtc || _index.Created != info.CreationTimeUtc)
            {
                _index = BuildIndex(session.Path, token); _pages.Clear();
            }
            if (invalidated) cursor = null;
            var messageIndex = cursor?.Message ?? 0; var character = cursor?.Character ?? 0;
            if (messageIndex < 0 || messageIndex > _index.Messages.Count || character < 0) throw new InvalidDataException("Invalid preview cursor.");
            var key = (messageIndex, character);
            if (_pages.TryGetValue(key, out var page)) return page with { Reloaded = invalidated };
            var text = new StringBuilder(PreviewPageCharacters);
            using var stream = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            while (messageIndex < _index.Messages.Count && text.Length < PreviewPageCharacters)
            {
                token.ThrowIfCancellationRequested();
                var range = _index.Messages[messageIndex];
                if (range.End - range.Start > IncrementalJsonlReader.MaxLineBytes) throw new InvalidDataException("Preview record is too large.");
                stream.Position = range.Start;
                var bytes = new byte[(int)(range.End - range.Start)]; stream.ReadExactly(bytes);
                using var json = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0));
                var message = range.Event ? new BrowserMessage(Get(json.RootElement, "timestamp"), "user", EventText(json.RootElement)) : Response(json.RootElement);
                var formatted = message is null ? "" : $"{message.Role.ToUpperInvariant()}  {message.Timestamp}\n{message.Text}\n\n";
                if (character > formatted.Length) throw new InvalidDataException("Preview cursor no longer matches the message.");
                var length = Math.Min(PreviewPageCharacters - text.Length, formatted.Length - character);
                if (length > 0 && character + length < formatted.Length && char.IsHighSurrogate(formatted[character + length - 1])) length--;
                if (length == 0 && character < formatted.Length) break;
                text.Append(formatted, character, length); character += length;
                if (character == formatted.Length) { messageIndex++; character = 0; }
            }
            BrowserPreviewCursor? next = messageIndex < _index.Messages.Count ? new(session.Path, _index.Length, _index.Written.Ticks, messageIndex, character) : null;
            info.Refresh();
            if (info.Length != _index.Length || info.LastWriteTimeUtc != _index.Written) { _index = null; _pages.Clear(); throw new IOException("Conversation changed while reading; reload the preview."); }
            page = new(text.ToString(), next, invalidated, _index.Truncated);
            if (_pages.Count >= 3) _pages.Remove(_pages.Keys.First());
            _pages[key] = page with { Reloaded = false };
            return page;
        }
    }

    private PreviewIndex BuildIndex(string path, CancellationToken token)
    {
        var info = new FileInfo(path); var responses = new List<MessageRange>(); var events = new List<MessageRange>();
        var reader = new IncrementalJsonlReader(); var capped = false; long characters = 0;
        reader.Read(path, (record, from, to) =>
        {
            // Index offsets only; no conversation bodies survive indexing.
            if (characters >= 32 * 1024 * 1024) { capped = true; return; }
            characters += reader.RecordCharacters + 1;
            if (responses.Count >= 1000) { capped = true; return; }
            if (GoalProtocol.Text(record, "type") == "response_item")
            {
                if (Response(record) is not null) responses.Add(new(from, to, false));
            }
            else if (events.Count < 1000 && EventText(record).Length > 0) events.Add(new(from, to, true));
        }, () => { responses.Clear(); events.Clear(); }, token, maxBytes: 128 * 1024 * 1024);
        return new(path, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, responses.Count > 0 ? responses : events, capped || info.Length > 128 * 1024 * 1024);
    }
}

using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexUsageAssistant.Services;

// A cursor retains offsets and small fingerprints, never conversation text.
internal sealed class IncrementalJsonlReader(long initialTailBytes = 0)
{
    private long _length = -1;
    private DateTime _written, _created;
    private byte[] _head = [], _checkpoint = [];
    internal long Offset { get; private set; }
    internal long BytesRead { get; private set; }
    internal int RecordCharacters { get; private set; }
    internal const int MaxLineBytes = 8 * 1024 * 1024;

    internal void Read(string path, Action<JsonElement, long, long> consume, Action reset, CancellationToken token,
        Func<ReadOnlyMemory<byte>, bool>? filter = null, long maxBytes = long.MaxValue)
    {
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
        var end = Math.Min(file.Length, maxBytes);
        var head = Fingerprint(file, 0, (int)Math.Min(_length < 0 ? end : Math.Min(end, _length), 128));
        var checkpoint = Fingerprint(file, Math.Max(0, Offset - 128), (int)Math.Min(128, Offset));
        var changed = _length < 0 || info.CreationTimeUtc != _created || end < _length ||
            end == _length && info.LastWriteTimeUtc != _written || !_head.AsSpan().SequenceEqual(head) ||
            !_checkpoint.AsSpan().SequenceEqual(checkpoint);
        if (changed) { Offset = initialTailBytes > 0 ? Math.Max(0, end - initialTailBytes) : 0; reset(); }
        if (!changed && end == Offset) return;
        file.Position = Offset;
        var skip = changed && Offset > 0;
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var line = new MemoryStream();
        var overflow = false; var start = Offset;
        try
        {
            while (file.Position < end)
            {
                token.ThrowIfCancellationRequested();
                var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, end - file.Position));
                if (read == 0) break;
                BytesRead += read;
                var batchStart = file.Position - read;
                for (var i = 0; i < read;)
                {
                    var newline = Array.IndexOf(buffer, (byte)'\n', i, read - i);
                    var stop = newline < 0 ? read : newline;
                    if (!skip && !overflow)
                    {
                        if (line.Length + stop - i <= MaxLineBytes) line.Write(buffer, i, stop - i);
                        else overflow = true;
                    }
                    if (newline < 0) break;
                    var next = batchStart + newline + 1;
                    if (!skip && !overflow) Parse(line, start, next, false);
                    Offset = next; start = next; skip = false; overflow = false;
                    line.SetLength(0);
                    if (line.Capacity > 256 * 1024) line.Capacity = 0;
                    i = newline + 1;
                }
            }
            // A valid final JSON value is readable without a newline; incomplete UTF-8/JSON is retried.
            if (!skip && !overflow && line.Length > 0 && Parse(line, start, end, true)) Offset = end;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _length = end; _written = info.LastWriteTimeUtc; _created = info.CreationTimeUtc;
            _head = Fingerprint(file, 0, (int)Math.Min(end, 128));
            _checkpoint = Fingerprint(file, Math.Max(0, Offset - 128), (int)Math.Min(128, Offset));
        }
        bool Parse(MemoryStream data, long from, long to, bool partial)
        {
            token.ThrowIfCancellationRequested();
            var bytes = data.GetBuffer().AsMemory(0, (int)data.Length);
            if (from == 0 && bytes.Span.StartsWith(new byte[] { 239, 187, 191 })) bytes = bytes[3..];
            if (bytes.Length == 0) return true;
            // A filter may skip complete records, but incomplete final lines must first be validated.
            if (!partial && filter is not null && !filter(bytes)) return true;
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (filter is null || filter(bytes))
                { RecordCharacters = System.Text.Encoding.UTF8.GetCharCount(bytes.Span); consume(document.RootElement, from, to); }
                return true;
            }
            catch (JsonException) { return !partial; }
        }
    }

    private static byte[] Fingerprint(FileStream file, long offset, int count)
    {
        if (count == 0) return [];
        Span<byte> bytes = stackalloc byte[128]; file.Position = offset;
        var read = file.Read(bytes[..count]);
        return SHA256.HashData(bytes[..read]);
    }
}

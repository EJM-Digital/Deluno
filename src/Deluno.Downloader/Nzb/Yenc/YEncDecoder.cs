using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Deluno.Downloader.Nzb.Yenc;

/// <summary>
/// Decodes a single NNTP article body in yEnc format.
///
/// Headers (single-part):  =ybegin line=128 size=N name=foo
///                         &lt;encoded&gt;
///                         =yend size=N crc32=HEX
///
/// Headers (multi-part):   =ybegin part=K total=M line=128 size=TOTAL name=foo
///                         =ypart begin=lo end=hi
///                         &lt;encoded&gt;
///                         =yend size=PART_BYTES part=K pcrc32=HEX
///
/// Encoding: out_byte = (in_byte + 42) mod 256; "critical" bytes
/// (NUL, LF, CR, '=') get prefixed with '=' and an additional +64
/// shift so they don't collide with NNTP transport.
///
/// Span&lt;byte&gt;-based hot loop. 8-bit byte handling everywhere — no
/// String / ASCII / StreamReader trips (the bug the spike caught).
/// </summary>
public static class YEncDecoder
{
    public static YEncArticle Decode(ReadOnlySpan<byte> articleBody)
    {
        var begin = ReadHeader(articleBody, "=ybegin", out var afterBegin)
            ?? throw new InvalidDataException("Missing =ybegin header.");
        var rest = articleBody[afterBegin..];

        var name = begin.GetString("name") ?? throw new InvalidDataException("=ybegin missing name.");
        var declaredSize = begin.GetInt64("size") ?? throw new InvalidDataException("=ybegin missing size.");
        var part = begin.GetInt32("part");
        var total = begin.GetInt32("total");

        long? partBegin = null, partEnd = null;
        if (part is not null)
        {
            var ypart = ReadHeader(rest, "=ypart", out var afterPart)
                ?? throw new InvalidDataException("Multi-part article missing =ypart header.");
            partBegin = ypart.GetInt64("begin");
            partEnd = ypart.GetInt64("end");
            rest = rest[afterPart..];
        }

        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(rest.Length);
        long decoded = 0;
        YEncHeader? endHeader = null;

        try
        {
            var i = 0;
            while (i < rest.Length)
            {
                var lineEnd = IndexOfLineEnd(rest, i);
                var line = rest[i..lineEnd];

                if (StartsWith(line, "=yend"u8))
                {
                    endHeader = ParseHeader(line);
                    break;
                }

                decoded += DecodeLine(line, buffer.AsSpan((int)decoded));
                i = SkipLineEnd(rest, lineEnd);
            }

            if (endHeader is null)
                throw new InvalidDataException("Missing =yend header.");

            var payload = new byte[decoded];
            buffer.AsSpan(0, (int)decoded).CopyTo(payload);

            var endSize = endHeader.GetInt64("size");
            if (endSize is not null && endSize.Value != decoded)
                throw new InvalidDataException(
                    $"yEnc size mismatch: =yend declared {endSize.Value} but decoded {decoded} bytes.");

            var pcrc32 = endHeader.GetUInt32Hex("pcrc32");
            var crc32 = endHeader.GetUInt32Hex("crc32");
            var expected = part is null ? crc32 : (pcrc32 ?? crc32);
            if (expected is not null)
            {
                var actual = Crc32.HashToUInt32(payload);
                if (actual != expected.Value)
                    throw new InvalidDataException(
                        $"yEnc CRC32 mismatch: expected {expected.Value:X8} got {actual:X8}.");
            }

            return new YEncArticle(
                Name: name,
                DeclaredSize: declaredSize,
                Part: part,
                Total: total,
                PartBegin: partBegin,
                PartEnd: partEnd,
                Crc32: crc32,
                PartialCrc32: pcrc32,
                Payload: payload);
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private static int DecodeLine(ReadOnlySpan<byte> src, Span<byte> dest)
    {
        var w = 0;
        for (var i = 0; i < src.Length; i++)
        {
            var b = src[i];
            if (b == (byte)'=')
            {
                if (++i >= src.Length)
                    throw new InvalidDataException("yEnc escape '=' at end of line.");
                dest[w++] = (byte)((src[i] - 64 - 42) & 0xFF);
            }
            else
            {
                dest[w++] = (byte)((b - 42) & 0xFF);
            }
        }
        return w;
    }

    private static YEncHeader? ReadHeader(ReadOnlySpan<byte> body, string tag, out int afterIndex)
    {
        afterIndex = 0;
        var lineEnd = IndexOfLineEnd(body, 0);
        var line = body[..lineEnd];
        var tagBytes = Encoding.ASCII.GetBytes(tag);
        if (!StartsWith(line, tagBytes)) return null;
        afterIndex = SkipLineEnd(body, lineEnd);
        return ParseHeader(line);
    }

    private static YEncHeader ParseHeader(ReadOnlySpan<byte> line)
    {
        var space = line.IndexOf((byte)' ');
        if (space < 0) return new YEncHeader(new Dictionary<string, string>(StringComparer.Ordinal));

        var rest = line[(space + 1)..];
        var text = Encoding.ASCII.GetString(rest);
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);

        // 'name' value can contain spaces; always scan as the last token.
        var nameIdx = text.IndexOf("name=", StringComparison.Ordinal);
        var prefix = nameIdx >= 0 ? text[..nameIdx] : text;
        var nameValue = nameIdx >= 0 ? text[(nameIdx + "name=".Length)..].Trim() : null;

        foreach (var token in prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            attrs[token[..eq]] = token[(eq + 1)..];
        }
        if (nameValue is not null) attrs["name"] = nameValue;
        return new YEncHeader(attrs);
    }

    private static int IndexOfLineEnd(ReadOnlySpan<byte> body, int start)
    {
        for (var i = start; i < body.Length; i++)
        {
            var b = body[i];
            if (b == (byte)'\r' || b == (byte)'\n') return i;
        }
        return body.Length;
    }

    private static int SkipLineEnd(ReadOnlySpan<byte> body, int idx)
    {
        if (idx < body.Length && body[idx] == (byte)'\r') idx++;
        if (idx < body.Length && body[idx] == (byte)'\n') idx++;
        return idx;
    }

    private static bool StartsWith(ReadOnlySpan<byte> line, ReadOnlySpan<byte> tag)
        => line.Length >= tag.Length && line[..tag.Length].SequenceEqual(tag);
}

internal sealed class YEncHeader(Dictionary<string, string> attrs)
{
    public string? GetString(string key) => attrs.TryGetValue(key, out var v) ? v : null;
    public int? GetInt32(string key)
        => attrs.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    public long? GetInt64(string key)
        => attrs.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    public uint? GetUInt32Hex(string key)
        => attrs.TryGetValue(key, out var v) && uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : null;
}

public sealed record YEncArticle(
    string Name,
    long DeclaredSize,
    int? Part,
    int? Total,
    long? PartBegin,
    long? PartEnd,
    uint? Crc32,
    uint? PartialCrc32,
    byte[] Payload);

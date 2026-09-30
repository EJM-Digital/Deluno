using System.Security.Cryptography;
using System.Text;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// The info-hash of a torrent, worked out before the torrent is sent anywhere,
/// so Deluno knows the id every torrent client files the download under
/// without asking the client to name it (#1062), the way Radarr and Sonarr do.
///
/// <para>Always lower-case hex, 40 characters: the v1 SHA-1 of the bencoded
/// <c>info</c> dictionary, or, for a torrent with no v1 pieces (BEP 52), the
/// first 20 bytes of its SHA-256, which is the id clients file such a torrent
/// under.</para>
/// </summary>
public static class TorrentInfoHash
{
    /// <summary>
    /// A hash as an indexer or a magnet writes it, made into the one form
    /// Deluno keeps: 40 hex characters or 32 base32 characters become
    /// lower-case hex. Anything else is not an info-hash (a Usenet GUID, say)
    /// and gives null.
    /// </summary>
    public static string? Normalise(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length == 40 && text.All(Uri.IsHexDigit))
        {
            return text.ToLowerInvariant();
        }

        if (text.Length == 32)
        {
            var bytes = FromBase32(text);
            return bytes is { Length: 20 } ? Convert.ToHexString(bytes).ToLowerInvariant() : null;
        }

        return null;
    }

    /// <summary>
    /// The info-hash a magnet link names: the <c>btih</c> topic (hex or base32),
    /// or, for a v2-only magnet, the <c>btmh</c> multihash cut to the 20 bytes
    /// clients file it under. Null when the text is not a magnet naming one.
    /// </summary>
    public static string? FromMagnet(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet)
            || !magnet.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = magnet[(magnet.IndexOf('?') + 1)..];
        string? v2 = null;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0 || !part[..separator].Equals("xt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var topic = Uri.UnescapeDataString(part[(separator + 1)..]);
            if (topic.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
            {
                if (Normalise(topic["urn:btih:".Length..]) is { } v1)
                {
                    return v1;
                }
            }
            else if (topic.StartsWith("urn:btmh:1220", StringComparison.OrdinalIgnoreCase) && topic.Length == "urn:btmh:1220".Length + 64)
            {
                var digest = topic["urn:btmh:1220".Length..];
                if (digest.All(Uri.IsHexDigit))
                {
                    v2 = digest[..40].ToLowerInvariant();
                }
            }
        }

        return v2;
    }

    /// <summary>
    /// The info-hash of a .torrent file's bytes, or null with the reason in
    /// <paramref name="problem"/> when the bytes are not a torrent Deluno can
    /// read.
    /// </summary>
    public static string? FromTorrentFile(byte[] file, out string? problem)
    {
        problem = null;
        try
        {
            var root = new Bencode(file).ReadRoot();
            if (root.InfoSpan is not { } span)
            {
                problem = "the file has no info section";
                return null;
            }

            var info = file.AsSpan(span.Start, span.Length);
            // A torrent with v1 pieces is filed under its SHA-1, hybrids
            // included. One with only v2 file trees has no v1 hash at all.
            if (root.InfoHasPieces || !root.InfoIsV2)
            {
                return Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant();
            }

            return Convert.ToHexString(SHA256.HashData(info)[..20]).ToLowerInvariant();
        }
        catch (FormatException exception)
        {
            problem = exception.Message;
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Anything else a hostile or broken file can provoke is still just
            // a file that is not a torrent (#1062).
            _ = exception;
            problem = "the file is not a torrent";
            return null;
        }
    }

    private static byte[]? FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>(20);
        var buffer = 0;
        var bits = 0;
        foreach (var character in text.ToUpperInvariant())
        {
            var value = alphabet.IndexOf(character);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return output.ToArray();
    }

    /// <summary>What the top level of a .torrent says that the hash needs.</summary>
    internal sealed record BencodeRoot((int Start, int Length)? InfoSpan, bool InfoHasPieces, bool InfoIsV2);

    /// <summary>
    /// A small bencode reader: it only has to walk a .torrent's dictionary and
    /// report where the <c>info</c> value starts and ends, byte for byte, since
    /// the hash is taken over exactly those bytes.
    /// </summary>
    internal sealed class Bencode(byte[] data)
    {
        private const int MaxDepth = 64;
        private int position;

        public BencodeRoot ReadRoot()
        {
            if (data.Length == 0 || data[0] != (byte)'d')
            {
                throw new FormatException("the file is not a torrent (a torrent starts with a dictionary)");
            }

            position = 1;
            (int Start, int Length)? span = null;
            var hasPieces = false;
            var isV2 = false;
            while (true)
            {
                if (position >= data.Length)
                {
                    throw new FormatException("the torrent ends before its dictionary does");
                }

                if (data[position] == (byte)'e')
                {
                    position++;
                    break;
                }

                var key = Encoding.Latin1.GetString(ReadString());
                var start = position;
                if (key == "info")
                {
                    if (position >= data.Length || data[position] != (byte)'d')
                    {
                        throw new FormatException("the info section is not a dictionary");
                    }

                    (hasPieces, isV2) = ReadInfoFlags();
                    span = (start, position - start);
                }
                else
                {
                    Skip(0);
                }
            }

            return new BencodeRoot(span, hasPieces, isV2);
        }

        /// <summary>Walks the info dictionary once, noting whether it has v1 pieces and a v2 meta version.</summary>
        private (bool HasPieces, bool IsV2) ReadInfoFlags()
        {
            position++; // 'd'
            var hasPieces = false;
            var isV2 = false;
            while (true)
            {
                if (position >= data.Length)
                {
                    throw new FormatException("the info section ends too soon");
                }

                if (data[position] == (byte)'e')
                {
                    position++;
                    return (hasPieces, isV2);
                }

                var key = Encoding.Latin1.GetString(ReadString());
                if (key == "meta version")
                {
                    isV2 = ReadInt() == 2;
                }
                else
                {
                    if (key == "pieces")
                    {
                        hasPieces = true;
                    }

                    Skip(1);
                }
            }
        }

        private void Skip(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("the torrent is nested too deeply");
            }

            if (position >= data.Length)
            {
                throw new FormatException("the torrent ends too soon");
            }

            switch ((char)data[position])
            {
                case 'i':
                    ReadInt();
                    break;
                case 'l':
                    position++;
                    while (Peek() != (byte)'e')
                    {
                        Skip(depth + 1);
                    }

                    position++;
                    break;
                case 'd':
                    position++;
                    while (Peek() != (byte)'e')
                    {
                        ReadString();
                        Skip(depth + 1);
                    }

                    position++;
                    break;
                case >= '0' and <= '9':
                    ReadString();
                    break;
                default:
                    throw new FormatException($"unexpected byte at position {position}");
            }
        }

        private byte Peek()
            => position < data.Length ? data[position] : throw new FormatException("the torrent ends too soon");

        private long ReadInt()
        {
            if (Peek() != (byte)'i')
            {
                throw new FormatException("expected a number");
            }

            position++;
            var end = Array.IndexOf(data, (byte)'e', position);
            if (end < 0 || end == position)
            {
                throw new FormatException("a number has no end");
            }

            var text = Encoding.ASCII.GetString(data, position, end - position);
            if (!long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException("a number is not a number");
            }

            position = end + 1;
            return value;
        }

        private byte[] ReadString()
        {
            var colon = Array.IndexOf(data, (byte)':', position);
            if (colon < 0 || colon == position)
            {
                throw new FormatException("a string has no length");
            }

            var text = Encoding.ASCII.GetString(data, position, colon - position);
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length) || length < 0)
            {
                throw new FormatException("a string has a bad length");
            }

            var start = colon + 1;
            if (length > data.Length - start)
            {
                throw new FormatException("a string runs past the end of the torrent");
            }

            position = start + length;
            return data.AsSpan(start, length).ToArray();
        }
    }
}

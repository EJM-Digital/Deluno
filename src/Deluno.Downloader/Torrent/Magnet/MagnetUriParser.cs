using System.Web;

namespace Deluno.Downloader.Torrent.Magnet;

/// <summary>
/// Magnet URI parser. The magnet URI scheme is an out-of-band
/// convention (not a BEP); we parse the well-known parameters we care
/// about and ignore the rest.
///
/// Format: <c>magnet:?xt=urn:btih:&lt;hex|base32&gt;[&amp;xt=urn:btmh:1220&lt;hex&gt;][&amp;dn=&lt;name&gt;][&amp;tr=&lt;url&gt;]+[&amp;ws=&lt;url&gt;]+</c>
///
/// Critically: a magnet does NOT carry the <c>private=1</c> flag —
/// that lives only inside the .torrent's info dict. The orchestrator
/// uses category / source-hint metadata to decide whether to gate
/// metadata fetch behind tracker-only mode (see architecture doc
/// §Magnet handling for potentially-private torrents).
/// </summary>
public static class MagnetUriParser
{
    public static MagnetLinkData Parse(string magnetUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(magnetUri);
        if (!magnetUri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Magnet URI must start with 'magnet:?'.");

        var query = magnetUri["magnet:?".Length..];
        var pairs = HttpUtility.ParseQueryString(query);

        string? infohashV1 = null;
        string? infohashV2 = null;
        var trackers = new List<string>();
        var webSeeds = new List<string>();
        string? displayName = null;

        foreach (string? key in pairs)
        {
            if (key is null) continue;
            var values = pairs.GetValues(key) ?? Array.Empty<string>();
            switch (key.ToLowerInvariant())
            {
                case "xt":
                    foreach (var v in values)
                    {
                        if (v.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                            infohashV1 = NormalizeInfohashV1(v["urn:btih:".Length..]);
                        else if (v.StartsWith("urn:btmh:1220", StringComparison.OrdinalIgnoreCase))
                            // BEP-52: btmh:1220<64 hex> (multihash sha2-256)
                            infohashV2 = v["urn:btmh:1220".Length..].ToLowerInvariant();
                    }
                    break;
                case "tr":
                    foreach (var v in values) trackers.Add(v);
                    break;
                case "ws":
                    foreach (var v in values) webSeeds.Add(v);
                    break;
                case "dn":
                    if (values.Length > 0) displayName = values[0];
                    break;
                // Other params (xl, xs, kt) ignored.
            }
        }

        if (infohashV1 is null && infohashV2 is null)
            throw new FormatException("Magnet URI must include an xt=urn:btih: or xt=urn:btmh: infohash.");

        return new MagnetLinkData(infohashV1, infohashV2, displayName, trackers, webSeeds);
    }

    /// <summary>
    /// Normalizes a BEP-9 infohash to lowercase 40-char hex.
    /// Some magnets ship the infohash as base32 (32 chars) — convert
    /// per BEP-9 spec.
    /// </summary>
    private static string NormalizeInfohashV1(string raw)
    {
        raw = raw.Trim();
        return raw.Length switch
        {
            40 => raw.ToLowerInvariant(),
            32 => Base32ToHex(raw),
            _ => throw new FormatException($"Magnet infohash must be 40-char hex or 32-char base32; got length {raw.Length}.")
        };
    }

    private static string Base32ToHex(string base32)
    {
        // RFC 4648 base32, no padding required (BEP-9 magnets typically omit).
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var input = base32.TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(input.Length * 5 / 8);
        int bits = 0, accum = 0;
        foreach (var c in input)
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException($"Invalid base32 character '{c}' in magnet infohash.");
            accum = (accum << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((accum >> bits) & 0xFF));
            }
        }
        return Convert.ToHexString(bytes.ToArray()).ToLowerInvariant();
    }
}

public sealed record MagnetLinkData(
    string? InfohashV1Hex,
    string? InfohashV2Hex,
    string? DisplayName,
    IReadOnlyList<string> Trackers,
    IReadOnlyList<string> WebSeeds)
{
    public bool HasTrackers => Trackers.Count > 0;

    /// <summary>
    /// Heuristic: if every listed tracker URL host looks like a private
    /// tracker (passkey-in-path is the strongest hint), the orchestrator
    /// should use tracker-only metadata fetch to avoid leaking the
    /// infohash to public DHT/PEX before we can see the .torrent's
    /// private flag.
    ///
    /// "Strong hint" = path contains a long random token (passkey).
    /// </summary>
    public bool LooksPrivate
    {
        get
        {
            if (Trackers.Count == 0) return false;
            foreach (var t in Trackers)
            {
                if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) continue;
                var path = uri.AbsolutePath;
                // Passkeys are typically 20+ char alphanumeric tokens
                // in the URL path (e.g. /<passkey>/announce).
                if (path.Length >= 22 &&
                    path.AsSpan().Trim('/').IndexOf('/') > 0 &&
                    HasLongTokenInPath(path))
                    return true;
            }
            return false;
        }
    }

    private static bool HasLongTokenInPath(string path)
    {
        foreach (var seg in path.Trim('/').Split('/'))
        {
            if (seg.Length < 20) continue;
            // All chars are URL-safe (likely a passkey).
            var ok = true;
            foreach (var c in seg)
            {
                if (!char.IsLetterOrDigit(c)) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }
}

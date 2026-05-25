using Deluno.Downloader.Torrent.Magnet;

namespace Deluno.Downloader.Tests.Torrent;

public class MagnetUriParserTests
{
    [Fact]
    public void Parses_simple_v1_magnet_with_display_name_and_trackers()
    {
        const string uri = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&dn=ubuntu-24.04-desktop-amd64.iso&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=udp%3A%2F%2Ftracker.openbittorrent.com%3A6969";
        var m = MagnetUriParser.Parse(uri);

        Assert.Equal("c12fe1c06bba254a9dc9f519b335aa7c1367a88a", m.InfohashV1Hex);
        Assert.Null(m.InfohashV2Hex);
        Assert.Equal("ubuntu-24.04-desktop-amd64.iso", m.DisplayName);
        Assert.Equal(2, m.Trackers.Count);
        Assert.Contains("udp://tracker.opentrackr.org:1337", m.Trackers);
    }

    [Fact]
    public void Normalizes_base32_infohash_to_hex()
    {
        // Same infohash as above (c12fe1c06bba254a9dc9f519b335aa7c1367a88a)
        // expressed as base32 per BEP-9 alternative. 40-char hex = 20 bytes;
        // base32 of 20 bytes = 32 chars.
        var b32 = Base32EncodeFromHex("c12fe1c06bba254a9dc9f519b335aa7c1367a88a");
        var uri = $"magnet:?xt=urn:btih:{b32}";

        var m = MagnetUriParser.Parse(uri);
        Assert.Equal("c12fe1c06bba254a9dc9f519b335aa7c1367a88a", m.InfohashV1Hex);
    }

    [Fact]
    public void Parses_v2_btmh_infohash()
    {
        // BEP-52 BTv2: urn:btmh:1220<64-hex sha256>
        var sha256Hex = new string('a', 64);
        var uri = $"magnet:?xt=urn:btmh:1220{sha256Hex}";
        var m = MagnetUriParser.Parse(uri);
        Assert.Equal(sha256Hex, m.InfohashV2Hex);
    }

    [Fact]
    public void Throws_on_missing_xt()
    {
        Assert.Throws<FormatException>(() => MagnetUriParser.Parse("magnet:?dn=name&tr=http://tracker"));
    }

    [Fact]
    public void Throws_on_wrong_scheme()
    {
        Assert.Throws<FormatException>(() => MagnetUriParser.Parse("http://example.com/?xt=urn:btih:..."));
    }

    [Fact]
    public void Throws_on_invalid_infohash_length()
    {
        Assert.Throws<FormatException>(() => MagnetUriParser.Parse("magnet:?xt=urn:btih:abc"));
    }

    [Fact]
    public void LooksPrivate_returns_true_when_tracker_url_carries_a_passkey()
    {
        const string uri = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&tr=https%3A%2F%2Ftracker.private-site.example%2Fa1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4%2Fannounce";
        var m = MagnetUriParser.Parse(uri);
        Assert.True(m.LooksPrivate);
    }

    [Fact]
    public void LooksPrivate_returns_false_for_public_trackers()
    {
        const string uri = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337";
        var m = MagnetUriParser.Parse(uri);
        Assert.False(m.LooksPrivate);
    }

    // Helper: convert hex to base32 (RFC 4648, no padding) for the
    // round-trip test above. Mirrors the parser's reverse function.
    private static string Base32EncodeFromHex(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new System.Text.StringBuilder((bytes.Length * 8 + 4) / 5);
        int bits = 0, accum = 0;
        foreach (var b in bytes)
        {
            accum = (accum << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(alphabet[(accum >> bits) & 0x1F]);
            }
        }
        if (bits > 0) sb.Append(alphabet[(accum << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }
}

using System.Security.Cryptography;
using System.Text;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1062: Deluno works out a torrent's info-hash itself before sending it, so
/// every grab is known by its id without asking the client to name anything.
/// The .torrent structures here are built the way real ones are (sorted keys,
/// 20-byte piece hashes, the info dictionary embedded byte for byte).
/// </summary>
public sealed class TorrentInfoHashTests
{
    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static readonly byte[] Pieces = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();

    private static byte[] V1Info() => Concat(Latin1("d6:lengthi5e4:name1:a12:piece lengthi16384e6:pieces20:"), Pieces, Latin1("e"));

    private static byte[] V2Info() => Concat(
        Latin1("d9:file treed1:ad0:d6:lengthi5e11:pieces root32:"),
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
        Latin1("eee12:meta versioni2e4:name1:a12:piece lengthi16384ee"));

    private static byte[] HybridInfo() => Concat(
        Latin1("d9:file treed1:ad0:d6:lengthi5e11:pieces root32:"),
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
        Latin1("eee6:lengthi5e12:meta versioni2e4:name1:a12:piece lengthi16384e6:pieces20:"),
        Pieces,
        Latin1("e"));

    private static byte[] Torrent(byte[] info) => Concat(
        Latin1("d8:announce21:http://t.example/ann.13:creation datei1700000000e4:info"),
        info,
        Latin1("7:comment5:hello"), // keys after info are read past, not hashed
        Latin1("e"));

    [Fact]
    public void The_v1_hash_is_the_sha1_of_the_info_dictionary_exactly_as_written()
    {
        var hash = TorrentInfoHash.FromTorrentFile(Torrent(V1Info()), out var problem);

        Assert.Null(problem);
        Assert.Equal("de4158016fbc49ebf35641903cec65a0ad0d30b5", hash);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(V1Info())).ToLowerInvariant(), hash);
    }

    [Fact]
    public void A_torrent_whose_info_comes_last_and_one_with_keys_after_it_give_the_same_hash()
    {
        var last = Concat(Latin1("d4:info"), V1Info(), Latin1("e"));

        Assert.Equal(
            TorrentInfoHash.FromTorrentFile(last, out _),
            TorrentInfoHash.FromTorrentFile(Torrent(V1Info()), out _));
    }

    [Fact]
    public void A_v2_only_torrent_is_filed_under_the_first_twenty_bytes_of_its_sha256()
    {
        var hash = TorrentInfoHash.FromTorrentFile(Torrent(V2Info()), out var problem);

        Assert.Null(problem);
        Assert.Equal("2e835fd97648f244cfdd35a7de21b288db211a43", hash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(V2Info())).ToLowerInvariant()[..40], hash);
    }

    [Fact]
    public void A_hybrid_torrent_is_filed_under_its_v1_sha1()
    {
        var hash = TorrentInfoHash.FromTorrentFile(Torrent(HybridInfo()), out _);

        Assert.Equal("c03abbec9c6a974f59d6f12b37ceb6d49eccadd2", hash);
    }

    [Theory]
    [InlineData("<html>Not a torrent</html>")]
    [InlineData("")]
    [InlineData("d4:name1:ae")]
    [InlineData("d4:infod6:lengthi5e")]
    [InlineData("d4:infoi5ee")]
    [InlineData("d4:infod4:name99:shorte")]
    public void Bytes_that_are_not_a_readable_torrent_give_a_reason_and_no_hash(string text)
    {
        var hash = TorrentInfoHash.FromTorrentFile(Latin1(text), out var problem);

        Assert.Null(hash);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Theory]
    [InlineData("2147483647")]
    [InlineData("2147483646")]
    [InlineData("2147483640")]
    [InlineData("4294967295")]
    [InlineData("99999999999")]
    [InlineData("-1")]
    public void A_string_length_near_the_int_limit_is_a_file_that_is_not_a_torrent_and_never_throws(string length)
    {
        var file = Latin1("d4:infod4:name" + length + ":shorte");
        var hash = TorrentInfoHash.FromTorrentFile(file, out var problem);

        Assert.Null(hash);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void Random_bytes_and_a_torrent_with_every_byte_cut_never_throw()
    {
        var random = new Random(1062);
        for (var round = 0; round < 500; round++)
        {
            var bytes = new byte[random.Next(0, 200)];
            random.NextBytes(bytes);
            Assert.Null(Record.Exception(() => TorrentInfoHash.FromTorrentFile(bytes, out _)));
        }

        var whole = Torrent(V1Info());
        for (var cut = 0; cut < whole.Length; cut++)
        {
            var part = whole[..cut];
            Assert.Null(Record.Exception(() => TorrentInfoHash.FromTorrentFile(part, out _)));
        }
    }

    [Fact]
    public void A_deeply_nested_file_is_refused_rather_than_overflowing_the_stack()
    {
        var nested = new string('l', 10_000);

        Assert.Null(TorrentInfoHash.FromTorrentFile(Latin1("d4:infod1:x" + nested + "ee"), out var problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("DE4158016FBC49EBF35641903CEC65A0AD0D30B5", "de4158016fbc49ebf35641903cec65a0ad0d30b5")]
    [InlineData("  de4158016fbc49ebf35641903cec65a0ad0d30b5 ", "de4158016fbc49ebf35641903cec65a0ad0d30b5")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "0000000000000000000000000000000000000000")]
    [InlineData("7777777777777777777777777777777", null)]
    [InlineData("77777777777777777777777777777777", "ffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("not-a-hash", null)]
    [InlineData("3f2a9c1e-77aa-4b1c-9d3e-000000000001", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_hash_as_an_indexer_writes_it_becomes_lower_case_hex(string? written, string? expected)
        => Assert.Equal(expected, TorrentInfoHash.Normalise(written));

    [Fact]
    public void A_base32_magnet_hash_and_the_hex_one_for_the_same_torrent_agree()
    {
        var hex = "de4158016fbc49ebf35641903cec65a0ad0d30b5";
        var base32 = ToBase32(Convert.FromHexString(hex));

        Assert.Equal(hex, TorrentInfoHash.FromMagnet($"magnet:?xt=urn:btih:{base32}&dn=x"));
        Assert.Equal(hex, TorrentInfoHash.FromMagnet($"magnet:?dn=x&xt=urn:btih:{hex.ToUpperInvariant()}&tr=udp%3A%2F%2Ft"));
    }

    [Fact]
    public void A_v2_only_magnet_uses_the_multihash_cut_to_twenty_bytes_and_a_link_that_is_not_a_magnet_names_nothing()
    {
        var digest = new string('a', 64);

        Assert.Equal(new string('a', 40), TorrentInfoHash.FromMagnet($"magnet:?xt=urn:btmh:1220{digest}"));
        Assert.Null(TorrentInfoHash.FromMagnet("https://indexer.example/download/1.torrent"));
        Assert.Null(TorrentInfoHash.FromMagnet("magnet:?dn=only-a-name"));
        Assert.Null(TorrentInfoHash.FromMagnet(null));
    }

    private static string ToBase32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var builder = new StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        return builder.ToString();
    }
}

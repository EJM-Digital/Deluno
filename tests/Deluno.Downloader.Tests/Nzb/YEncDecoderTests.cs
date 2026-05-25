using Deluno.Downloader.Nzb.Yenc;

namespace Deluno.Downloader.Tests.Nzb;

public class YEncDecoderTests
{
    [Fact]
    public void Decodes_single_part_article_round_trip()
    {
        var payload = new byte[1024];
        new Random(42).NextBytes(payload);

        var encoded = YEncTestEncoder.EncodeSinglePart("cool.bin", payload);
        var article = YEncDecoder.Decode(encoded);

        Assert.Equal("cool.bin", article.Name);
        Assert.Null(article.Part);
        Assert.Equal(payload.Length, article.DeclaredSize);
        Assert.Equal(payload, article.Payload);
    }

    [Fact]
    public void Decodes_escape_sequences()
    {
        var payload = new byte[] { 0x00, 0x0A, 0x0D, 0x3D, 0x01, 0xFF, 0x2A };
        var encoded = YEncTestEncoder.EncodeSinglePart("escape.bin", payload);
        var article = YEncDecoder.Decode(encoded);
        Assert.Equal(payload, article.Payload);
    }

    [Fact]
    public void Detects_size_mismatch()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var encoded = YEncTestEncoder.EncodeSinglePart("x.bin", payload, declaredSizeOverride: 999);
        Assert.Throws<InvalidDataException>(() => YEncDecoder.Decode(encoded));
    }

    [Fact]
    public void Detects_crc_mismatch()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var encoded = YEncTestEncoder.EncodeSinglePart("x.bin", payload, crcOverride: 0xDEADBEEF);
        Assert.Throws<InvalidDataException>(() => YEncDecoder.Decode(encoded));
    }

    [Fact]
    public void Decodes_multipart_article_with_pcrc32()
    {
        var full = new byte[10000];
        new Random(7).NextBytes(full);
        var p1 = full[..3000];
        var p2 = full[3000..7000];
        var p3 = full[7000..];

        var enc1 = YEncTestEncoder.EncodeMultiPart("big.bin", full.Length, 1, 3, 1, 3000, p1);
        var enc2 = YEncTestEncoder.EncodeMultiPart("big.bin", full.Length, 2, 3, 3001, 7000, p2);
        var enc3 = YEncTestEncoder.EncodeMultiPart("big.bin", full.Length, 3, 3, 7001, full.Length, p3);

        var a1 = YEncDecoder.Decode(enc1);
        var a2 = YEncDecoder.Decode(enc2);
        var a3 = YEncDecoder.Decode(enc3);

        Assert.Equal(p1, a1.Payload);
        Assert.Equal(p2, a2.Payload);
        Assert.Equal(p3, a3.Payload);

        var reassembled = a1.Payload.Concat(a2.Payload).Concat(a3.Payload).ToArray();
        Assert.Equal(full, reassembled);
    }

    [Fact]
    public void Throws_on_missing_ybegin()
    {
        var body = "this is not yenc\r\n=yend size=0\r\n"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => YEncDecoder.Decode(body));
    }

    [Fact]
    public void Throws_on_missing_yend()
    {
        var body = "=ybegin line=128 size=5 name=x.bin\r\n*+,-.\r\n"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => YEncDecoder.Decode(body));
    }
}

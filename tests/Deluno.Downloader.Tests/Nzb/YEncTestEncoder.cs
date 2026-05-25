using System.IO.Hashing;
using System.Text;

namespace Deluno.Downloader.Tests.Nzb;

/// <summary>
/// Minimal yEnc encoder used by tests to round-trip the decoder.
/// Writes raw bytes (yEnc produces 8-bit output; do NOT go through any
/// string/ASCII conversion or bytes &gt; 127 get mangled to '?' — the
/// bug that originally bit the spike).
/// </summary>
internal static class YEncTestEncoder
{
    private const int LineLength = 128;

    public static byte[] EncodeSinglePart(string name, byte[] payload, long? declaredSizeOverride = null, uint? crcOverride = null)
    {
        using var ms = new MemoryStream();
        WriteAscii(ms, $"=ybegin line={LineLength} size={payload.Length} name={name}\r\n");
        WriteEncodedPayload(ms, payload);
        var crc = crcOverride ?? Crc32.HashToUInt32(payload);
        WriteAscii(ms, $"=yend size={declaredSizeOverride ?? payload.Length} crc32={crc:x8}\r\n");
        return ms.ToArray();
    }

    public static byte[] EncodeMultiPart(string name, long totalSize, int part, int total, long begin, long end, byte[] payload)
    {
        using var ms = new MemoryStream();
        WriteAscii(ms, $"=ybegin part={part} total={total} line={LineLength} size={totalSize} name={name}\r\n");
        WriteAscii(ms, $"=ypart begin={begin} end={end}\r\n");
        WriteEncodedPayload(ms, payload);
        var pcrc = Crc32.HashToUInt32(payload);
        WriteAscii(ms, $"=yend size={payload.Length} part={part} pcrc32={pcrc:x8}\r\n");
        return ms.ToArray();
    }

    private static void WriteAscii(MemoryStream ms, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        ms.Write(bytes, 0, bytes.Length);
    }

    private static void WriteEncodedPayload(MemoryStream ms, byte[] payload)
    {
        var col = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            var b = (byte)((payload[i] + 42) & 0xFF);
            if (b == 0x00 || b == 0x0A || b == 0x0D || b == (byte)'=')
            {
                ms.WriteByte((byte)'=');
                ms.WriteByte((byte)((b + 64) & 0xFF));
                col += 2;
            }
            else
            {
                ms.WriteByte(b);
                col++;
            }
            if (col >= LineLength)
            {
                ms.WriteByte(0x0D);
                ms.WriteByte(0x0A);
                col = 0;
            }
        }
        if (col > 0)
        {
            ms.WriteByte(0x0D);
            ms.WriteByte(0x0A);
        }
    }
}

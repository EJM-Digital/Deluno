namespace Deluno.Downloader.Nzb.Nntp;

/// <summary>
/// Reads CRLF-terminated byte lines from a stream WITHOUT text-decoding.
/// Reserves an internal byte buffer; returns a freshly-allocated byte[]
/// per line (caller may retain).
///
/// Critical: NNTP article bodies are 8-bit binary. Using
/// <see cref="System.IO.StreamReader"/> on the body decodes through
/// ASCII and silently clamps any byte &gt; 127 to '?', which corrupts
/// every yEnc article. This reader was added to fix that bug in the
/// spike.
/// </summary>
internal sealed class ByteLineReader(Stream stream)
{
    private byte[] _buf = new byte[16 * 1024];
    private int _start;
    private int _end;

    public async Task<ReadOnlyMemory<byte>?> ReadLineAsync(CancellationToken ct)
    {
        var pieces = new List<byte[]>();
        var lineLength = 0;

        while (true)
        {
            for (var i = _start; i < _end; i++)
            {
                if (_buf[i] != 0x0A) continue;

                var lfIndex = i;
                var contentEnd = lfIndex;
                if (contentEnd > _start && _buf[contentEnd - 1] == 0x0D) contentEnd--;

                if (pieces.Count == 0)
                {
                    var line = new byte[contentEnd - _start];
                    Array.Copy(_buf, _start, line, 0, line.Length);
                    _start = lfIndex + 1;
                    return line;
                }
                else
                {
                    var tailLen = contentEnd - _start;
                    var full = new byte[lineLength + tailLen];
                    var offset = 0;
                    foreach (var chunk in pieces)
                    {
                        Array.Copy(chunk, 0, full, offset, chunk.Length);
                        offset += chunk.Length;
                    }
                    Array.Copy(_buf, _start, full, offset, tailLen);
                    _start = lfIndex + 1;
                    return full;
                }
            }

            if (_end > _start)
            {
                var chunk = new byte[_end - _start];
                Array.Copy(_buf, _start, chunk, 0, chunk.Length);
                pieces.Add(chunk);
                lineLength += chunk.Length;
            }
            _start = 0;
            _end = 0;
            var read = await stream.ReadAsync(_buf.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return null; // EOF
            _end = read;
        }
    }
}

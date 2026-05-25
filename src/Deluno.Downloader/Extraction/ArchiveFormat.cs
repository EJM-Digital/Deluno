namespace Deluno.Downloader.Extraction;

/// <summary>
/// Recognised archive formats. Detection is done by file extension first
/// (cheap, reliable for our domain) with magic-byte sniffing as a
/// fallback for ambiguous cases (e.g. <c>.bin</c> that's actually a RAR).
/// </summary>
public enum ArchiveFormat
{
    Unknown,
    Zip,
    SevenZip,
    Tar,
    TarGz,
    TarBz2,
    Rar,         // RAR3 + RAR5; extracted via UnRAR binary (Phase 4)
}

public static class ArchiveFormatDetector
{
    /// <summary>
    /// Cheap extension-based detection. Returns <see cref="ArchiveFormat.Unknown"/>
    /// when no extension match — callers can then attempt magic-byte
    /// detection or skip the file.
    /// </summary>
    public static ArchiveFormat DetectByExtension(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower switch
        {
            var p when p.EndsWith(".zip", StringComparison.Ordinal) => ArchiveFormat.Zip,
            var p when p.EndsWith(".7z", StringComparison.Ordinal) => ArchiveFormat.SevenZip,
            var p when p.EndsWith(".tar.gz", StringComparison.Ordinal) || p.EndsWith(".tgz", StringComparison.Ordinal) => ArchiveFormat.TarGz,
            var p when p.EndsWith(".tar.bz2", StringComparison.Ordinal) || p.EndsWith(".tbz2", StringComparison.Ordinal) => ArchiveFormat.TarBz2,
            var p when p.EndsWith(".tar", StringComparison.Ordinal) => ArchiveFormat.Tar,
            var p when p.EndsWith(".rar", StringComparison.Ordinal) => ArchiveFormat.Rar,
            // Multi-volume RAR3 (.r00, .r01, ...) — only the .rar entry-point matters; volume parts get
            // pulled in by the RAR extractor itself.
            var p when System.Text.RegularExpressions.Regex.IsMatch(p, @"\.r\d{2,}$") => ArchiveFormat.Rar,
            // Multi-volume RAR5 (.part1.rar, .part01.rar, ...) — same comment.
            var p when System.Text.RegularExpressions.Regex.IsMatch(p, @"\.part\d+\.rar$") => ArchiveFormat.Rar,
            _ => ArchiveFormat.Unknown,
        };
    }

    /// <summary>
    /// Magic-byte sniffing fallback for files whose extension doesn't
    /// match. Reads up to 8 bytes from the start of the file; returns
    /// <see cref="ArchiveFormat.Unknown"/> if nothing matches.
    /// </summary>
    public static async Task<ArchiveFormat> DetectByMagicAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var buf = new byte[8];
        var read = await fs.ReadAsync(buf, ct);
        if (read < 4) return ArchiveFormat.Unknown;

        // Zip:     50 4B 03 04 (or 05 06 for empty)
        if (buf[0] == 0x50 && buf[1] == 0x4B && (buf[2] == 0x03 || buf[2] == 0x05) && (buf[3] == 0x04 || buf[3] == 0x06))
            return ArchiveFormat.Zip;
        // 7z:      37 7A BC AF 27 1C
        if (read >= 6 && buf[0] == 0x37 && buf[1] == 0x7A && buf[2] == 0xBC && buf[3] == 0xAF && buf[4] == 0x27 && buf[5] == 0x1C)
            return ArchiveFormat.SevenZip;
        // RAR5:    52 61 72 21 1A 07 01 00
        if (read >= 8 && buf[0] == 0x52 && buf[1] == 0x61 && buf[2] == 0x72 && buf[3] == 0x21
            && buf[4] == 0x1A && buf[5] == 0x07 && buf[6] == 0x01 && buf[7] == 0x00)
            return ArchiveFormat.Rar;
        // RAR3/4:  52 61 72 21 1A 07 00
        if (read >= 7 && buf[0] == 0x52 && buf[1] == 0x61 && buf[2] == 0x72 && buf[3] == 0x21
            && buf[4] == 0x1A && buf[5] == 0x07 && buf[6] == 0x00)
            return ArchiveFormat.Rar;
        // Gzip:    1F 8B (may be .tar.gz or just gzip)
        if (buf[0] == 0x1F && buf[1] == 0x8B) return ArchiveFormat.TarGz;
        return ArchiveFormat.Unknown;
    }
}

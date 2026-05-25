using System.Diagnostics;

namespace Deluno.Downloader.Extraction;

/// <summary>
/// RAR3 / RAR5 extraction via the bundled <c>UnRAR.exe</c> / <c>unrar</c>
/// binary. Extraction-only use of the official UnRAR is permitted by the
/// rarlab license; document in <c>NOTICE</c>.
///
/// Phase 2 ships the wrapper interface and a thin Process shell-out;
/// Phase 4 ships the actual binaries in the Velopack installer payload
/// and the Docker image (<c>apt install unrar</c> / per-platform binary
/// directory). Until then, <see cref="ExtractAsync"/> returns a failure
/// result with an actionable message when the binary isn't on PATH or
/// in <paramref name="binaryPath"/>.
/// </summary>
public sealed class UnRarBinaryExtractor : IArchiveExtractor
{
    public IReadOnlyCollection<ArchiveFormat> Supports { get; } = [ArchiveFormat.Rar];

    private readonly string _binaryPath;

    /// <summary>
    /// Creates a wrapper that invokes <c>unrar</c> at <paramref name="binaryPath"/>.
    /// Pass <c>"unrar"</c> (or <c>"UnRAR.exe"</c>) to use PATH resolution.
    /// </summary>
    public UnRarBinaryExtractor(string binaryPath = "unrar") => _binaryPath = binaryPath;

    public async Task<ArchiveExtractionResult> ExtractAsync(
        string archivePath,
        string outputDir,
        string? password,
        IProgress<ArchiveExtractionProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var extractedFiles = new List<string>();

        // unrar x -y -o+ [-pPASSWORD] archive.rar dest/
        var args = new List<string> { "x", "-y", "-o+" };
        if (!string.IsNullOrEmpty(password)) args.Add($"-p{password}");
        args.Add(archivePath);
        args.Add(outputDir.EndsWith(Path.DirectorySeparatorChar) ? outputDir : outputDir + Path.DirectorySeparatorChar);

        var psi = new ProcessStartInfo
        {
            FileName = _binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new ArchiveExtractionResult(
                Succeeded: false,
                ExtractedFiles: extractedFiles,
                FailureReason:
                    $"Could not launch unrar binary '{_binaryPath}': {ex.Message}. " +
                    "Bundle UnRAR.exe / unrar with Deluno (Phase 4 work) or install it system-wide.");
        }

        if (proc is null)
            return new ArchiveExtractionResult(false, extractedFiles, $"Process.Start returned null for '{_binaryPath}'.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            return new ArchiveExtractionResult(
                Succeeded: false,
                ExtractedFiles: extractedFiles,
                FailureReason: $"unrar exit {proc.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
        }

        // Best-effort: enumerate what we created (unrar's stdout has the
        // file list but parsing it is fragile across versions; just walk
        // the output dir).
        foreach (var f in Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories))
            extractedFiles.Add(f);

        progress?.Report(new ArchiveExtractionProgress(Path.GetFileName(archivePath), 100, 100));
        return new ArchiveExtractionResult(true, extractedFiles, null);
    }
}

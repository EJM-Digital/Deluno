namespace Deluno.Downloader.Nzb.Par2;

/// <summary>
/// Wraps an out-of-process par2 implementation (par2cmdline-turbo). We
/// shell out rather than implement par2 in managed code — par2 is a
/// gnarly Reed-Solomon-over-GF(2^16) implementation that already exists
/// in well-tested form and adding our own is a large risk surface for
/// no benefit.
///
/// Binary bundling per-platform is installer / release work; this
/// service is the protocol surface that the orchestrator uses.
/// </summary>
public interface IPar2Service
{
    /// <summary>
    /// Checks whether the par2 binary is present at the resolved path
    /// (config → PATH → bundled). Surfaced in the diagnostics endpoint
    /// so users see at a glance whether NZB repair is available.
    /// </summary>
    Task<Par2BinaryStatus> CheckBinaryAsync(CancellationToken ct);

    /// <summary>
    /// Verifies the par2 recovery set against its declared payload
    /// files. Pass the path to any *.par2 file in the set (typically
    /// the small "index" .par2); par2cmdline finds the rest of the
    /// volumes automatically by name pattern.
    /// </summary>
    Task<Par2VerifyResult> VerifyAsync(
        string par2File,
        IProgress<Par2Progress>? progress,
        CancellationToken ct);

    /// <summary>
    /// Attempts to repair the payload files from the par2 recovery
    /// blocks. Idempotent: if files are already complete returns
    /// success without doing work.
    /// </summary>
    Task<Par2RepairResult> RepairAsync(
        string par2File,
        IProgress<Par2Progress>? progress,
        CancellationToken ct);
}

public sealed record Par2BinaryStatus(bool Found, string? ResolvedPath, string? Version, string? ErrorMessage);

public sealed record Par2VerifyResult(Par2Outcome Outcome, string? Message);

public sealed record Par2RepairResult(bool Repaired, string? Message);

public sealed record Par2Progress(double Percent, string CurrentFile);

/// <summary>par2cmdline / par2cmdline-turbo standard exit codes.</summary>
public enum Par2Outcome
{
    /// <summary>Exit 0 — verification succeeded; files are complete.</summary>
    Ok,
    /// <summary>Exit 1 — files damaged but recoverable with available recovery blocks.</summary>
    NeedsRepair,
    /// <summary>Exit 2 — files damaged AND not enough recovery blocks to repair.</summary>
    UnrecoverableDamage,
    /// <summary>Exit 3 — required source files are missing entirely.</summary>
    MissingFiles,
    /// <summary>Exit non-standard or the wrapper itself failed.</summary>
    Failed,
}

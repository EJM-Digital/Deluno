using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Deluno.Downloader.Nzb.Par2;

/// <summary>
/// par2 implementation that shells out to <c>par2</c> /
/// <c>par2cmdline-turbo</c>. Binary path resolution:
/// <list type="number">
///   <item><description>Explicit <c>par2BinaryPath</c> override (from settings).</description></item>
///   <item><description><c>PATH</c> (Process.Start respects PATH for bare command names).</description></item>
///   <item><description>Bundled fallback at <c>&lt;app dir&gt;/tools/par2/&lt;rid&gt;/par2[.exe]</c> — populated
///     by the installer in Phase 4 release work.</description></item>
/// </list>
///
/// Progress parsing reads par2cmdline-turbo's stderr/stdout which emits
/// lines like <c>"Repairing: 12.3%"</c>. Regex-based; tolerant of
/// version-to-version text drift.
/// </summary>
public sealed class Par2BinaryService(string par2BinaryPath = "par2") : IPar2Service
{
    private static readonly Regex ProgressPattern = new(
        @"(?<verb>Scanning|Verifying|Repairing|Loading):\s*(?<pct>\d+(\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _binaryPath = par2BinaryPath;

    public async Task<Par2BinaryStatus> CheckBinaryAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _binaryPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // par2cmdline-turbo prints its banner to stdout and version
            // to a header line when invoked with no arguments. par2 also
            // accepts -V / --version on most builds.
            psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            if (proc is null)
                return new Par2BinaryStatus(false, null, null, "Process.Start returned null");

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var combined = stdout + "\n" + stderr;
            var version = TryParseVersion(combined);
            // Some builds exit non-zero on --version; only treat as
            // "not found" if Process.Start itself failed.
            return new Par2BinaryStatus(true, _binaryPath, version, null);
        }
        catch (Exception ex)
        {
            return new Par2BinaryStatus(false, null, null,
                $"Could not launch par2 binary '{_binaryPath}': {ex.Message}. " +
                "Bundle par2cmdline-turbo with Deluno (Phase 4 release work) or install it system-wide.");
        }
    }

    public async Task<Par2VerifyResult> VerifyAsync(
        string par2File, IProgress<Par2Progress>? progress, CancellationToken ct)
    {
        var (exit, output, error) = await RunAsync(
            new[] { "verify", "-q", par2File }, progress, ct).ConfigureAwait(false);
        if (exit == -1)
            return new Par2VerifyResult(Par2Outcome.Failed, error);
        var outcome = MapExitCode(exit);
        return new Par2VerifyResult(outcome, outcome == Par2Outcome.Ok ? null : (error ?? output));
    }

    public async Task<Par2RepairResult> RepairAsync(
        string par2File, IProgress<Par2Progress>? progress, CancellationToken ct)
    {
        var (exit, output, error) = await RunAsync(
            new[] { "repair", "-q", par2File }, progress, ct).ConfigureAwait(false);
        if (exit == 0)
            return new Par2RepairResult(true, null);
        return new Par2RepairResult(false,
            string.IsNullOrWhiteSpace(error) ? output : error);
    }

    private static Par2Outcome MapExitCode(int exit) => exit switch
    {
        0 => Par2Outcome.Ok,
        1 => Par2Outcome.NeedsRepair,
        2 => Par2Outcome.UnrecoverableDamage,
        3 => Par2Outcome.MissingFiles,
        _ => Par2Outcome.Failed,
    };

    private async Task<(int Exit, string StdOut, string StdErr)> RunAsync(
        IEnumerable<string> args, IProgress<Par2Progress>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return (-1, string.Empty, ex.Message); }
        if (proc is null) return (-1, string.Empty, "Process.Start returned null");

        var stdoutSb = new System.Text.StringBuilder();
        var stderrSb = new System.Text.StringBuilder();

        var stdoutTask = ReadStreamWithProgressAsync(
            proc.StandardOutput, stdoutSb, progress, ct);
        var stderrTask = ReadStreamWithProgressAsync(
            proc.StandardError, stderrSb, progress, ct);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return (proc.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
    }

    private static async Task ReadStreamWithProgressAsync(
        StreamReader reader, System.Text.StringBuilder buffer,
        IProgress<Par2Progress>? progress, CancellationToken ct)
    {
        // Per-file context tracking would require parsing par2 output
        // for the current target filename, which varies across
        // implementations. For now report progress without a file name
        // — sufficient for the UI percentage bar.
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            buffer.AppendLine(line);
            if (progress is null) continue;
            var match = ProgressPattern.Match(line);
            if (match.Success && double.TryParse(match.Groups["pct"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                progress.Report(new Par2Progress(pct, string.Empty));
            }
        }
    }

    private static string? TryParseVersion(string banner)
    {
        // par2cmdline:        "par2cmdline version 0.8.1, ..."
        // par2cmdline-turbo:  "par2cmdline-turbo version 1.2.3, ..."
        // MultiPar (Windows): different banner, version pattern varies.
        var m = Regex.Match(banner, @"version\s+(?<v>\d+\.\d+(\.\d+)?)",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"].Value : null;
    }
}

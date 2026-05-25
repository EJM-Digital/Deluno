namespace Deluno.Downloader.Postprocessing;

/// <summary>
/// Sequential runner over an ordered list of <see cref="IPostProcessor"/>
/// steps. Each step receives the output of the previous one. Used by the
/// orchestrator after extraction completes, before raising the
/// <c>ImportPending</c> lifecycle event.
///
/// Standard order:
/// <list type="number">
///   <item><description><see cref="SampleAndProofFilter"/> — drop preview clips, NFOs, sfv, url shortcuts.</description></item>
///   <item><description><see cref="SubdirectoryFlattener"/> (NZB by default; off for torrents) — move payload to the root.</description></item>
///   <item><description><see cref="FileNameSanitizer"/> — make filenames safe + disambiguate case.</description></item>
/// </list>
/// </summary>
public sealed class PostProcessingPipeline
{
    private readonly IReadOnlyList<IPostProcessor> _steps;

    public PostProcessingPipeline(IReadOnlyList<IPostProcessor> steps) => _steps = steps;

    public async Task<IReadOnlyList<string>> RunAsync(
        string workingDir,
        IReadOnlyList<string> files,
        CancellationToken ct)
    {
        var current = files;
        foreach (var step in _steps)
        {
            ct.ThrowIfCancellationRequested();
            current = await step.ProcessAsync(workingDir, current, ct);
        }
        return current;
    }
}

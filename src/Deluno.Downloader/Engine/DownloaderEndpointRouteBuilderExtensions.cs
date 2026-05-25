using Deluno.Downloader.Engine;
using Deluno.Downloader.Extraction;
using Deluno.Downloader.Nzb.Nntp;
using Deluno.Downloader.Nzb.Par2;
using Deluno.Downloader.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Downloader.Engine;

/// <summary>
/// HTTP surface for the built-in downloader:
/// <list type="bullet">
///   <item><description>CRUD over <c>nzb_servers</c> so the Settings UI can manage news servers.</description></item>
///   <item><description><c>GET /api/downloader/diagnostics</c> reports par2 / unrar binary health
///     so users can see whether the engine is actually able to verify-and-repair / extract.</description></item>
///   <item><description><c>GET /api/downloader/jobs</c> snapshot of active jobs across both protocols
///     (handy for debugging without going through the SAB-compatible telemetry shape).</description></item>
/// </list>
///
/// Test-connection (live NNTP handshake against a configured server) is
/// the next polish item; surfaces under the server-CRUD routes when
/// added.
/// </summary>
public static class DownloaderEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoDownloaderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/downloader").WithTags("Downloader");

        // ---------------- nzb_servers CRUD ----------------

        group.MapGet("/nzb-servers", async (INzbServerRepository repo, CancellationToken ct) =>
        {
            var servers = await repo.ListEnabledAsync(ct);
            return Results.Ok(servers.Select(ToDto));
        }).WithName("ListNzbServers");

        group.MapGet("/nzb-servers/{id}", async (string id, INzbServerRepository repo, CancellationToken ct) =>
        {
            var s = await repo.GetAsync(id, ct);
            return s is null ? Results.NotFound() : Results.Ok(ToDto(s));
        }).WithName("GetNzbServer");

        group.MapPut("/nzb-servers/{id}", async (
            string id, NzbServerDto body, INzbServerRepository repo, CancellationToken ct) =>
        {
            if (!string.Equals(id, body.Id, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Path id and body id must match.");
            await repo.UpsertAsync(FromDto(body), ct);
            return Results.Ok(body);
        }).WithName("UpsertNzbServer");

        group.MapPost("/nzb-servers", async (
            NzbServerDto body, INzbServerRepository repo, CancellationToken ct) =>
        {
            var withId = body.Id is { Length: > 0 } ? body : body with { Id = Guid.NewGuid().ToString("N") };
            await repo.UpsertAsync(FromDto(withId), ct);
            return Results.Created($"/api/downloader/nzb-servers/{withId.Id}", withId);
        }).WithName("CreateNzbServer");

        group.MapDelete("/nzb-servers/{id}", async (string id, INzbServerRepository repo, CancellationToken ct) =>
        {
            await repo.RemoveAsync(id, ct);
            return Results.NoContent();
        }).WithName("DeleteNzbServer");

        // ---------------- diagnostics ----------------

        group.MapGet("/diagnostics", async (
            IPar2Service par2,
            IEnumerable<IArchiveExtractor> extractors,
            IJobRepository jobs,
            CancellationToken ct) =>
        {
            var par2Status = await par2.CheckBinaryAsync(ct);
            // Active jobs across all states except terminal Done/Failed.
            var active = await jobs.ListByStateAsync(
                new[] {
                    JobLifecycleState.Queued, JobLifecycleState.Fetching, JobLifecycleState.Reassembled,
                    JobLifecycleState.Verify, JobLifecycleState.Verified, JobLifecycleState.Repair,
                    JobLifecycleState.Extracting, JobLifecycleState.Extracted, JobLifecycleState.PostProcessed,
                    JobLifecycleState.ImportPending, JobLifecycleState.Seeding, JobLifecycleState.Paused,
                }, limit: 200, ct);

            return Results.Ok(new
            {
                par2 = new
                {
                    found = par2Status.Found,
                    resolvedPath = par2Status.ResolvedPath,
                    version = par2Status.Version,
                    error = par2Status.ErrorMessage,
                },
                extractors = extractors.SelectMany(e => e.Supports.Select(f => new
                {
                    format = f.ToString(),
                    impl = e.GetType().Name,
                })).ToList(),
                activeJobs = new
                {
                    total = active.Count,
                    byProtocol = active.GroupBy(j => j.Protocol.ToDbValue())
                        .ToDictionary(g => g.Key, g => g.Count()),
                    byState = active.GroupBy(j => j.State.ToString())
                        .ToDictionary(g => g.Key, g => g.Count()),
                },
            });
        }).WithName("GetDownloaderDiagnostics");

        // ---------------- direct job submission (debugging / live test) ----------------
        //
        // Bypasses the indexer → grab → download-client dispatch chain
        // and drops an NZB or torrent source straight into the Queued
        // bucket. The execution worker picks it up on its next tick.
        // Mainly for end-to-end testing without needing a configured
        // indexer; production grabs still flow through the normal
        // request pipeline.
        group.MapPost("/jobs", async (
            SubmitJobRequest body,
            IJobRepository jobs,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.SourcePath))
                return Results.BadRequest("sourcePath is required.");
            if (string.IsNullOrWhiteSpace(body.DownloadDir))
                return Results.BadRequest("downloadDir is required.");

            var protocol = string.Equals(body.Protocol, "torrent", StringComparison.OrdinalIgnoreCase)
                ? DownloadProtocol.Torrent
                : DownloadProtocol.Nzb;

            var now = time.GetUtcNow();
            var id = Guid.NewGuid().ToString("N");
            var displayName = string.IsNullOrWhiteSpace(body.DisplayName)
                ? Path.GetFileNameWithoutExtension(body.SourcePath)
                : body.DisplayName!;
            var sourceKind = body.SourceKind ?? (protocol == DownloadProtocol.Nzb ? "nzb" : "torrent_file");

            var job = new JobRecord(
                Id: id,
                Protocol: protocol,
                DisplayName: displayName,
                SourcePath: body.SourcePath,
                SourceKind: sourceKind,
                Category: body.Category,
                Priority: body.Priority ?? 0,
                State: JobLifecycleState.Queued,
                StateReason: "Submitted via /api/downloader/jobs",
                Paused: false,
                PasswordProtected: null,
                DownloadDir: body.DownloadDir,
                OutputDir: null,
                TotalBytes: 0,
                DownloadedBytes: 0,
                UploadedBytes: 0,
                DispatchId: null,
                LibraryId: null,
                CreatedAt: now,
                UpdatedAt: now,
                CompletedAt: null);
            await jobs.UpsertAsync(job, ct);
            return Results.Created($"/api/downloader/jobs/{id}", new { id, state = job.State.ToString() });
        }).WithName("SubmitDownloaderJob");

        group.MapGet("/jobs/{id}", async (string id, IJobRepository jobs, CancellationToken ct) =>
        {
            var j = await jobs.GetAsync(id, ct);
            if (j is null) return Results.NotFound();
            return Results.Ok(new
            {
                id = j.Id,
                protocol = j.Protocol.ToDbValue(),
                state = j.State.ToString(),
                stateReason = j.StateReason,
                displayName = j.DisplayName,
                category = j.Category,
                totalBytes = j.TotalBytes,
                downloadedBytes = j.DownloadedBytes,
                outputDir = j.OutputDir,
                createdAt = j.CreatedAt,
                updatedAt = j.UpdatedAt,
                completedAt = j.CompletedAt,
            });
        }).WithName("GetDownloaderJob");

        // ---------------- jobs snapshot (debugging) ----------------

        group.MapGet("/jobs", async (IJobRepository jobs, CancellationToken ct) =>
        {
            var active = await jobs.ListByStateAsync(
                new[] {
                    JobLifecycleState.Queued, JobLifecycleState.Fetching, JobLifecycleState.Reassembled,
                    JobLifecycleState.Verify, JobLifecycleState.Verified, JobLifecycleState.Repair,
                    JobLifecycleState.Extracting, JobLifecycleState.Extracted, JobLifecycleState.PostProcessed,
                    JobLifecycleState.ImportPending, JobLifecycleState.Seeding, JobLifecycleState.Paused,
                    JobLifecycleState.Failed,
                }, limit: 200, ct);
            return Results.Ok(active.Select(j => new
            {
                id = j.Id,
                protocol = j.Protocol.ToDbValue(),
                state = j.State.ToString(),
                stateReason = j.StateReason,
                displayName = j.DisplayName,
                category = j.Category,
                totalBytes = j.TotalBytes,
                downloadedBytes = j.DownloadedBytes,
                createdAt = j.CreatedAt,
                updatedAt = j.UpdatedAt,
            }));
        }).WithName("ListActiveDownloaderJobs");

        return endpoints;
    }

    /// <summary>
    /// Request body for POST /api/downloader/jobs.
    /// Required: <c>SourcePath</c> + <c>DownloadDir</c>. Everything else
    /// has sensible defaults that match Deluno's NZB-first conventions.
    /// </summary>
    public sealed record SubmitJobRequest(
        string SourcePath,            // NZB URL, .torrent URL, or magnet:?xt=...
        string DownloadDir,           // absolute path under which job.Id subdir is created
        string? Protocol = null,      // "nzb" (default) | "torrent"
        string? SourceKind = null,    // "nzb" | "torrent_file" | "magnet" — inferred from protocol if null
        string? DisplayName = null,   // defaults to basename(SourcePath)
        string? Category = null,
        int? Priority = null);

    public sealed record NzbServerDto(
        string Id,
        string Name,
        string Host,
        int Port,
        bool UseTls,
        string? Username,
        string? Password,
        int MaxConnections,
        int Priority,
        string Tier,            // "Primary" | "Backup" | "Fill"
        int? RetentionDays,
        bool Enabled);

    private static NzbServerDto ToDto(NntpServerOptions s) => new(
        Id: s.Id,
        Name: s.Name,
        Host: s.Host,
        Port: s.Port,
        UseTls: s.UseTls,
        Username: s.Username,
        // NEVER return the password through the API once stored. Empty
        // string signals "credentials present"; null signals "no creds".
        Password: string.IsNullOrEmpty(s.Password) ? null : string.Empty,
        MaxConnections: s.MaxConnections,
        Priority: s.Priority,
        Tier: s.Tier.ToString(),
        RetentionDays: s.RetentionDays,
        Enabled: s.Enabled);

    private static NntpServerOptions FromDto(NzbServerDto d) => new(
        Id: d.Id,
        Name: d.Name,
        Host: d.Host,
        Port: d.Port,
        UseTls: d.UseTls,
        Username: d.Username,
        Password: d.Password,
        MaxConnections: d.MaxConnections,
        Priority: d.Priority,
        Tier: Enum.Parse<NntpServerTier>(d.Tier),
        RetentionDays: d.RetentionDays,
        Enabled: d.Enabled);
}

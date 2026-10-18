using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker.Testing;

/// <summary>
/// Lets a browser test put a download Deluno has sent, and the hand-off to a processor that went with it, into the real
/// databases, so Transfers can be walked in every state it has without a download client and a processor running beside it.
/// What the screen then reads and every button it presses go through the real routes and the real repositories; only the
/// starting records are written here, with the same repository calls the work planner makes.
///
/// <para><b>Never reachable outside a smoke run.</b> Mapped only when <c>Testing:EnableTransfersSeedEndpoint</c> is true, which
/// the shipped app's configuration never sets (the same guard as the realtime push and the large-library seed routes): in a real
/// install the route is absent from the endpoint table, not merely unauthorized.</para>
/// </summary>
public static class TransfersSeedTestEndpoints
{
    public static IEndpointRouteBuilder MapDelunoTransfersSeedTestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/testing/transfers/handoff", async (
            HttpContext httpContext,
            [FromBody] SeedHandoffRequest request,
            IJobQueueRepository jobs,
            IDownloadDispatchesRepository dispatches,
            IProcessorRepository processors,
            IJobScheduler scheduler,
            Deluno.Movies.Data.IMovieCatalogRepository movies,
            IDelunoDatabaseConnectionFactory databases,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var mediaType = string.IsNullOrWhiteSpace(request.MediaType) ? "movies" : request.MediaType;
            var clientId = string.IsNullOrWhiteSpace(request.ClientId) ? "seeded-client" : request.ClientId;
            var queueItemId = string.IsNullOrWhiteSpace(request.QueueItemId) ? Guid.NewGuid().ToString("N") : request.QueueItemId;
            var entityType = mediaType == "tv" ? "series" : "movie";
            var entityId = string.IsNullOrWhiteSpace(request.EntityId) ? $"{entityType}-seeded-{Guid.NewGuid():N}" : request.EntityId;
            var sourcePath = string.IsNullOrWhiteSpace(request.SourcePath) ? Path.Combine(Path.GetTempPath(), "deluno-seeded", request.ReleaseName) : request.SourcePath;

            string? dispatchId = null;
            if (request.WithDispatch != false)
            {
                dispatchId = await jobs.RecordDownloadDispatchAsync(
                    request.LibraryId, mediaType, entityType, entityId, request.ReleaseName, "Seeded", clientId, "Seeded client", "sent", null,
                    cancellationToken: cancellationToken);
                await dispatches.RecordGrabAsync(dispatchId, "succeeded", 200, null, null, null, cancellationToken, externalId: queueItemId);
                await dispatches.RecordDetectionAsync(dispatchId, queueItemId, null, completed: true, cancellationToken);
            }

            string? importJobId = null;
            if (!string.IsNullOrWhiteSpace(request.ImportJob))
            {
                importJobId = (await scheduler.EnqueueAsync(
                    new EnqueueJobRequest("filesystem.import.execute", "seeded", "{}", entityType, entityId, IdempotencyKey: "seeded-import-" + queueItemId),
                    cancellationToken)).Id;
            }

            // No status means no hand-off: only the download and, if asked, an import job of its own.
            ProcessorHandoffItem? handoff = null;
            if (request.Status is not null)
            {
                handoff = await processors.EnsureProcessorHandoffAsync(
                    new CreateProcessorHandoffRequest(request.LibraryId, mediaType, clientId, queueItemId, request.ReleaseName, sourcePath, request.ProcessorName ?? "Weir"),
                    cancellationToken);
                await processors.UpdateProcessorHandoffAsync(handoff.Id, request.Status, request.OutputPath, importJobId, request.FailureMessage, cancellationToken);
            }

            if (importJobId is not null)
            {
                // The import ran and gave up after the processor handed its output back; or it is queued or running, held where the
                // job worker will not pick it up, so the screen can be read in that state for as long as a test needs.
                await Task.Delay(50, cancellationToken); // reason: a give-up must be stamped after the hand-off's last update, which is what the row compares
                var later = timeProvider.GetUtcNow().AddDays(30).ToString("O");
                var failedJob = request.ImportJob is "dead-letter" or "failed";
                await using var connection = await databases.OpenConnectionAsync("jobs", cancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE job_queue SET status = @status, scheduled_utc = @later, leased_until_utc = @lease, last_error = @error, last_attempt_utc = @now WHERE id = @id;";
                void Add(string name, object? value)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                Add("@status", request.ImportJob);
                Add("@later", later);
                Add("@lease", request.ImportJob == "running" ? later : null);
                Add("@error", failedJob ? "The destination file already exists." : null);
                Add("@now", timeProvider.GetUtcNow().ToString("O"));
                Add("@id", importJobId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            // The same failure told again by the other records behind it: the download's own failed import, and the recovery case raised about it.
            if (dispatchId is not null && request.DispatchImportFailure is { } importFailure)
            {
                await dispatches.RecordImportOutcomeAsync(dispatchId, "failed", null, "import-failed", importFailure, cancellationToken);
            }

            string? recoveryCaseId = null;
            if (dispatchId is not null && request.RecoveryCase == true)
            {
                var details = System.Text.Json.JsonSerializer.Serialize(new { DispatchId = dispatchId, HandoffId = handoff?.Id, ClientId = clientId, QueueItemId = queueItemId });
                recoveryCaseId = (await movies.AddImportRecoveryCaseAsync(
                    new Deluno.Movies.Contracts.CreateMovieImportRecoveryCaseRequest(request.ReleaseName, "importFailed", request.FailureMessage ?? "The import did not finish.", "Try the import again.", details),
                    cancellationToken)).Id;
            }

            if (handoff is not null && request.ProcessorState is { } state)
            {
                var now = timeProvider.GetUtcNow();
                await processors.RecordProcessorObservationAsync(
                    handoff!.Id,
                    new ProcessorHandoffObservation(state, request.ProcessorMessage, request.QueuePosition, request.ScheduledForUtc, now, now, request.Unreachable == true ? now : null, now),
                    cancellationToken);
            }

            return Results.Ok(new { handoffId = handoff?.Id, dispatchId, importJobId, recoveryCaseId, clientId, queueItemId });
        });

        // Puts the databases back as they were before the seeding: everything the route above wrote, and the lines Activity kept of acting on it.
        endpoints.MapPost("/api/testing/transfers/clear", async (
            HttpContext httpContext,
            IDelunoDatabaseConnectionFactory databases,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            async Task Run(string database, params string[] statements)
            {
                await using var connection = await databases.OpenConnectionAsync(database, cancellationToken);
                foreach (var statement in statements)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            try
            {
                await Run("platform", "DELETE FROM processor_handoffs WHERE source_path LIKE '%deluno-seeded%';");
                await Run("jobs", "DELETE FROM job_queue WHERE source = 'seeded';", "DELETE FROM download_dispatches WHERE indexer_name = 'Seeded';");
                await Run(
                    "movies",
                    "DELETE FROM movie_import_recovery_events WHERE case_id IN (SELECT id FROM movie_import_recovery_cases WHERE details_json LIKE '%dispatchId%');",
                    "DELETE FROM movie_import_recovery_cases WHERE details_json LIKE '%dispatchId%';");
            }
            catch (Exception exception)
            {
                return Results.Problem(exception.Message);
            }

            return Results.NoContent();
        });

        return endpoints;
    }

    public sealed record SeedHandoffRequest(
        string LibraryId,
        string ReleaseName,
        string? Status,
        string? FailureMessage,
        string? MediaType,
        string? ClientId,
        string? QueueItemId,
        string? EntityId,
        string? SourcePath,
        string? OutputPath,
        string? ProcessorName,
        bool? WithDispatch,
        string? ImportJob,
        string? ProcessorState,
        string? ProcessorMessage,
        int? QueuePosition,
        DateTimeOffset? ScheduledForUtc,
        bool? Unreachable,
        string? DispatchImportFailure = null,
        bool? RecoveryCase = null);
}

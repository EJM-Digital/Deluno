using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Api.Updates;

namespace Deluno.Api.Backup;

public static class BackupEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var backup = endpoints.MapGroup("/api/backups");

        backup.MapGet(string.Empty, async (IDelunoBackupService service, CancellationToken cancellationToken) =>
        {
            var items = await service.ListBackupsAsync(cancellationToken);
            return Results.Ok(items);
        });

        backup.MapGet("/settings", async (IDelunoBackupService service, CancellationToken cancellationToken) =>
        {
            var settings = await service.GetSettingsAsync(cancellationToken);
            return Results.Ok(settings);
        });

        backup.MapPut("/settings", async (
            [FromBody] UpdateBackupSettingsRequest request,
            IDelunoBackupService service,
            CancellationToken cancellationToken) =>
        {
            var settings = await service.SaveSettingsAsync(request, cancellationToken);
            return Results.Ok(settings);
        });

        backup.MapPost(string.Empty, async (
            [FromBody] BackupCreateRequest request,
            IDelunoBackupService service,
            CancellationToken cancellationToken) =>
        {
            var item = await service.CreateBackupAsync(request.Reason ?? "manual", cancellationToken);
            return Results.Ok(new BackupCreateResponse(item));
        });

        backup.MapGet("/{id}/download", async (
            string id,
            IDelunoBackupService service,
            CancellationToken cancellationToken) =>
        {
            var file = await service.OpenBackupAsync(id, cancellationToken);
            return file is null
                ? Results.NotFound()
                : Results.File(file.Value.Stream, file.Value.ContentType, file.Value.FileName);
        });

        backup.MapDelete("/{id}", async (
            string id,
            IDelunoBackupService service,
            CancellationToken cancellationToken) =>
        {
            var removed = await service.DeleteBackupAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        backup.MapPost("/restore/preview", async (
            IFormFile file,
            IDelunoBackupService service,
            CancellationToken cancellationToken) =>
        {
            await using var stream = file.OpenReadStream();
            var result = await service.PreviewRestoreAsync(stream, cancellationToken);
            return Results.Ok(result);
        }).DisableAntiforgery();

        backup.MapPost("/restore", async (
            IFormFile file,
            IDelunoBackupService service,
            CancellationToken cancellationToken) =>
        {
            await using var stream = file.OpenReadStream();
            var result = await service.RestoreAsync(stream, cancellationToken);
            return Results.Ok(result);
        }).DisableAntiforgery();

        var update = endpoints.MapGroup("/api/updates");

        update.MapGet("/status", async (
            IUpdateOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var status = await orchestrator.GetStatusAsync(cancellationToken);
            return Results.Ok(status);
        });

        update.MapGet("/preferences", async (
            IUpdateOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var preferences = await orchestrator.GetPreferencesAsync(cancellationToken);
            return Results.Ok(preferences);
        });

        update.MapPut("/preferences", async (
            [FromBody] UpdatePreferencesRequest request,
            IUpdateOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var preferences = await orchestrator.SavePreferencesAsync(request, cancellationToken);
            return Results.Ok(preferences);
        });

        update.MapPost("/check", async (
            IUpdateOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var status = await orchestrator.CheckForUpdatesAsync(cancellationToken);
            return Results.Ok(new UpdateActionResponse(true, "Checked for updates.", status));
        });

        update.MapPost("/download", async (
            IUpdateOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var status = await orchestrator.DownloadUpdatesAsync(cancellationToken);
            return Results.Ok(new UpdateActionResponse(true, "Download request completed.", status));
        });

        update.MapPost("/apply-on-restart", async (
            IUpdateOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var status = await orchestrator.PrepareApplyOnNextRestartAsync(cancellationToken);
            return Results.Ok(new UpdateActionResponse(true, "Update is prepared for restart.", status));
        });

        update.MapPost("/restart-now", async (
            IDelunoBackupService backupService,
            IUpdateOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await backupService.CreateBackupAsync("pre-update", cancellationToken);
            }
            catch (Exception ex)
            {
                var blocked = await orchestrator.GetStatusAsync(cancellationToken);
                return Results.Ok(new UpdateActionResponse(
                    Accepted: false,
                    Message: $"Backup failed and restart was blocked: {ex.Message}",
                    Status: blocked));
            }

            var status = await orchestrator.ApplyAndRestartNowAsync(cancellationToken);
            return Results.Ok(new UpdateActionResponse(
                Accepted: true,
                Message: "Backup completed. Restarting to apply update.",
                Status: status));
        });

        return endpoints;
    }
}

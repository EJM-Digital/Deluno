using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

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
            UpdateBackupSettingsRequest request,
            IDelunoBackupService service,
            CancellationToken cancellationToken) =>
        {
            var settings = await service.SaveSettingsAsync(request, cancellationToken);
            return Results.Ok(settings);
        });

        backup.MapPost(string.Empty, async (
            BackupCreateRequest request,
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

        update.MapGet("/status", (IConfiguration configuration) =>
        {
            var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
            var feedUrl = configuration["Deluno:Updates:FeedUrl"]
                ?? Environment.GetEnvironmentVariable("DELUNO_UPDATE_FEED_URL");
            var channel = configuration["Deluno:Updates:Channel"]
                ?? Environment.GetEnvironmentVariable("DELUNO_UPDATE_CHANNEL")
                ?? "stable";

            return Results.Ok(new UpdateStatusResponse(
                CurrentVersion: currentVersion,
                Channel: string.IsNullOrWhiteSpace(feedUrl) ? "manual" : channel,
                UpdateAvailable: false,
                LatestVersion: null,
                Message: string.IsNullOrWhiteSpace(feedUrl)
                    ? "In-app update checks are ready, but no signed release feed is configured."
                    : "A signed release feed is configured. Use check for updates to compare against the latest release manifest.",
                Notes:
                [
                    "Updates must be signed before Deluno should offer one-click install.",
                    "A backup should be created automatically before any future upgrade is applied.",
                    "Docker installs should surface image/tag guidance instead of replacing binaries in-place."
                ]));
        });

        update.MapPost("/check", async (
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
            var feedUrl = configuration["Deluno:Updates:FeedUrl"]
                ?? Environment.GetEnvironmentVariable("DELUNO_UPDATE_FEED_URL");
            var channel = configuration["Deluno:Updates:Channel"]
                ?? Environment.GetEnvironmentVariable("DELUNO_UPDATE_CHANNEL")
                ?? "stable";

            if (string.IsNullOrWhiteSpace(feedUrl))
            {
                return Results.Ok(new UpdateStatusResponse(
                    CurrentVersion: currentVersion,
                    Channel: "manual",
                    UpdateAvailable: false,
                    LatestVersion: null,
                    Message: "No signed update feed is configured yet.",
                    Notes:
                    [
                        "Set Deluno:Updates:FeedUrl or DELUNO_UPDATE_FEED_URL when release infrastructure exists.",
                        "Deluno will not download or apply updates until the manifest includes signature and checksum data."
                    ]));
            }

            try
            {
                using var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);
                await using var stream = await client.GetStreamAsync(feedUrl, cancellationToken);
                var manifest = await JsonSerializer.DeserializeAsync<UpdateFeedManifest>(
                    stream,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web),
                    cancellationToken);

                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                {
                    return Results.Ok(new UpdateStatusResponse(
                        CurrentVersion: currentVersion,
                        Channel: channel,
                        UpdateAvailable: false,
                        LatestVersion: null,
                        Message: "The update feed responded, but the release manifest was invalid.",
                        Notes: ["Expected version, channel, checksum, and signature fields."]));
                }

                var isSigned = !string.IsNullOrWhiteSpace(manifest.Signature)
                    && !string.IsNullOrWhiteSpace(manifest.Sha256)
                    && !string.IsNullOrWhiteSpace(manifest.DownloadUrl);
                var updateAvailable = IsVersionNewer(manifest.Version, currentVersion) && isSigned;
                var notes = new List<string>(manifest.Notes ?? []);
                if (!isSigned)
                {
                    notes.Add("Release manifest is missing download URL, SHA-256 checksum, or signature; update is informational only.");
                }

                return Results.Ok(new UpdateStatusResponse(
                    CurrentVersion: currentVersion,
                    Channel: manifest.Channel,
                    UpdateAvailable: updateAvailable,
                    LatestVersion: manifest.Version,
                    Message: updateAvailable
                        ? $"Version {manifest.Version} is available. Create a backup before applying it."
                        : $"Deluno is current for the {manifest.Channel} channel, or the feed is not fully signed.",
                    Notes: notes));
            }
            catch (Exception ex)
            {
                return Results.Ok(new UpdateStatusResponse(
                    CurrentVersion: currentVersion,
                    Channel: channel,
                    UpdateAvailable: false,
                    LatestVersion: null,
                    Message: "Could not check the update feed.",
                    Notes: [ex.Message]));
            }
        });

        return endpoints;
    }

    private static bool IsVersionNewer(string candidate, string current)
    {
        return Version.TryParse(candidate, out var candidateVersion)
            && Version.TryParse(current, out var currentVersion)
            && candidateVersion > currentVersion;
    }
}

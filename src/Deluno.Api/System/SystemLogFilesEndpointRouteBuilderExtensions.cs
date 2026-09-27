using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Deluno.Api.SystemInfo;

public sealed record LogFileItem(string Name, long SizeBytes, DateTimeOffset LastWrittenUtc);

public sealed record LogFilesFolderResponse(string Path);

/// <summary>
/// A Log Files list with download and open-folder actions (#867). Radarr's
/// own System > Logs page is the model: DelunoFileLogger already rolls and
/// retains daily log files under {DataRoot}/logs, but nothing on screen ever
/// listed, downloaded, or deleted one.
/// </summary>
public static class SystemLogFilesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoSystemLogFilesEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/system/log-files", (
                IOptions<StoragePathOptions> storage,
                IOptions<DelunoFileLogOptions> logOptions) =>
        {
            var directory = LogDirectory(storage, logOptions);
            if (!Directory.Exists(directory))
            {
                return Results.Ok(Array.Empty<LogFileItem>());
            }

            var files = new DirectoryInfo(directory)
                .EnumerateFiles($"{DelunoFileLoggerProvider.FilePrefix}*{DelunoFileLoggerProvider.FileExtension}")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new LogFileItem(file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)))
                .ToArray();
            return Results.Ok(files);
        })
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        // Deluno mostly runs headless or in a container (#867's own
        // fallback): rather than guess at an OS-specific "reveal in
        // Explorer" that would only ever work on a desktop install, this
        // always offers the folder path, which works everywhere.
        api.MapGet("/system/log-files/folder", (
                IOptions<StoragePathOptions> storage,
                IOptions<DelunoFileLogOptions> logOptions) =>
            Results.Ok(new LogFilesFolderResponse(LogDirectory(storage, logOptions))))
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        api.MapGet("/system/log-files/{name}/download", (
                string name,
                IOptions<StoragePathOptions> storage,
                IOptions<DelunoFileLogOptions> logOptions) =>
        {
            var path = ResolveSafePath(name, storage, logOptions);
            if (path is null || !File.Exists(path))
            {
                return Results.NotFound();
            }

            return Results.File(File.OpenRead(path), "application/x-ndjson", Path.GetFileName(path));
        })
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        api.MapDelete("/system/log-files/{name}", (
                string name,
                IOptions<StoragePathOptions> storage,
                IOptions<DelunoFileLogOptions> logOptions,
                TimeProvider timeProvider) =>
        {
            var path = ResolveSafePath(name, storage, logOptions);
            if (path is null || !File.Exists(path))
            {
                return Results.NotFound();
            }

            // The file for today's date is still being written to; deleting
            // it out from under the writer is the one thing this must never
            // do, the same safety automatic RetainDays cleanup already
            // gives every older file.
            var today = $"{DelunoFileLoggerProvider.FilePrefix}{timeProvider.GetUtcNow():yyyy-MM-dd}{DelunoFileLoggerProvider.FileExtension}";
            if (string.Equals(Path.GetFileName(path), today, StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Today's log file is still being written to and cannot be deleted."]
                });
            }

            File.Delete(path);
            return Results.NoContent();
        })
            .RequireAuthorization(DelunoAuthorizationPolicies.System);

        return api;
    }

    private static string LogDirectory(IOptions<StoragePathOptions> storage, IOptions<DelunoFileLogOptions> logOptions)
        => DelunoFileLoggerProvider.ResolveDirectory(storage.Value.DataRoot, logOptions.Value.Directory);

    /// <summary>
    /// Only a bare file name naming an actual Deluno log file resolves: no
    /// path traversal out of the log directory, and nothing that is not one
    /// of Deluno's own rolled files.
    /// </summary>
    private static string? ResolveSafePath(string name, IOptions<StoragePathOptions> storage, IOptions<DelunoFileLogOptions> logOptions)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains('/') || name.Contains('\\') ||
            !name.StartsWith(DelunoFileLoggerProvider.FilePrefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(DelunoFileLoggerProvider.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(LogDirectory(storage, logOptions), name);
    }
}

using System.Text.RegularExpressions;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Metadata;
using Deluno.Libraries.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Deluno.Worker.ApiParity;

/// <summary>Radarr's <c>DiskSpaceResource</c>: <c>path</c>, <c>label</c>, <c>freeSpace</c>, <c>totalSpace</c>, all in bytes.</summary>
public sealed record DiskSpaceResource(string Path, string Label, long FreeSpace, long TotalSpace);

/// <summary>
/// A standalone disk-space address and a find-by-IMDb-id address (#1021).
///
/// <para><b>Disk space.</b> Radarr's <c>DiskSpaceController</c> returns a plain
/// list of <c>DiskSpaceResource</c>, one per drive holding something it cares
/// about (root folders, its own data). Deluno does the same: one entry per
/// drive holding Deluno's data folder, a library's folder, or a library's
/// downloads folder. <c>path</c> is the first such folder found on that drive
/// and <c>label</c> says whose it is in plain words. Drives that cannot be
/// read are left out, not reported as zero.</para>
///
/// <para><b>IMDb lookup.</b> Radarr's <c>GET /movie/lookup/imdb?imdbId=</c>.
/// Deluno hands the id to the same identity lookup the Add screen uses
/// (<c>imdb:</c> ids), so it asserts an identity and never returns a fuzzy
/// match, then says whether Deluno already holds the film.</para>
/// </summary>
public static partial class DiskSpaceAndLookupEndpoints
{
    public static IEndpointRouteBuilder MapDelunoDiskSpaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/diskspace", async (
            IOptions<StoragePathOptions> storage,
            ILibrariesRepository libraries,
            CancellationToken cancellationToken) =>
        {
            var places = new List<(string Path, string Label)> { (System.IO.Path.GetFullPath(storage.Value.DataRoot), "Deluno's own data") };
            foreach (var library in await libraries.ListLibrariesAsync(cancellationToken))
            {
                places.Add((library.RootPath, library.Name));
                if (!string.IsNullOrWhiteSpace(library.DownloadsPath))
                {
                    places.Add((library.DownloadsPath, $"{library.Name} downloads"));
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<DiskSpaceResource>();
            foreach (var (path, label) in places)
            {
                if (VolumeSpace.TryRead(path) is { } reading && seen.Add(reading.VolumeKey))
                {
                    result.Add(new DiskSpaceResource(path, label, reading.FreeBytes, reading.TotalBytes));
                }
            }

            return Results.Ok(result);
        }).RequireAuthorization(DelunoAuthorizationPolicies.Read);

        endpoints.MapGet("/api/movies/lookup/imdb", async (
            string? imdbId,
            IMetadataProvider provider,
            IMetadataLibraryPresence libraryPresence,
            CancellationToken cancellationToken) =>
        {
            var id = imdbId?.Trim();
            if (string.IsNullOrEmpty(id) || !ImdbIdPattern().IsMatch(id))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["imdbId"] = ["Give an IMDb id such as tt0133093."]
                });
            }

            // A provider that is down (it throws, or answers nothing and says why) is a 503 with a plain sentence, never a 404
            // that reads as "no such film" and never an unhandled 500.
            const string OutageMessage = "The metadata service isn't answering; try again in a minute.";
            IReadOnlyList<MetadataSearchResult> found;
            using var failureScope = provider.BeginFailureScope();
            try
            {
                found = await provider.SearchAsync(
                    new MetadataLookupRequest(Query: null, MediaType: "movies", Year: null, ProviderId: id, IdSource: "imdb"),
                    cancellationToken);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                return Results.Json(new { error = "metadata-unavailable", message = OutageMessage }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (found.Count == 0 && failureScope.Failure is { } outage)
            {
                return Results.Json(new { error = "metadata-unavailable", message = MetadataFailureWords.Describe(outage) }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var film = found.FirstOrDefault(result => string.Equals(result.MediaType, "movies", StringComparison.OrdinalIgnoreCase));
            if (film is null)
            {
                return Results.NotFound(new { message = $"No film has the IMDb id {id}." });
            }

            return Results.Ok((await libraryPresence.MarkHeldTitlesAsync([film], cancellationToken))[0]);
        }).RequireAuthorization(DelunoAuthorizationPolicies.Read);

        return endpoints;
    }

    [GeneratedRegex("^tt[0-9]{5,10}$", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbIdPattern();
}

using Deluno.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Movies.Testing;

/// <summary>
/// Seeds a large synthetic film library for #934's "checked at scale"
/// acceptance line: thousands of titles spread across every status chip, so
/// the smoke suite can prove the chip row still shows all of them promptly
/// rather than trusting that from a dozen fixture rows.
///
/// <para><b>Never reachable outside a smoke run.</b> Mapped only when
/// <c>Testing:EnableLargeLibrarySeedEndpoint</c> is true — see
/// <c>DelunoApplicationEndpointMapping.MapDelunoApplicationEndpoints</c> —
/// which the shipped app's configuration never sets. The smoke suite's own
/// Playwright config sets it as an environment variable for its own
/// disposable API process only, so the route does not exist in the binary a
/// real install runs against, nor in a rig's own configuration.</para>
///
/// <para>Bypasses the real add-movie endpoint on purpose: that path enqueues a
/// metadata refresh and a search job per title (and, for a monitored,
/// searchable title, a dispatch), which would mean thousands of jobs the
/// worker has nothing real to do with. This writes the catalogue and
/// wanted-state rows through the same repository calls that endpoint makes —
/// <see cref="IMovieCatalogRepository.AddAsync"/> and
/// <see cref="IMovieCatalogRepository.EnsureWantedStateAsync"/> — and nothing
/// else, so the rows it produces are exactly what the catalogue page and its
/// facets already know how to read.</para>
/// </summary>
public static class MoviesLargeLibrarySeedEndpoints
{
    private const string TitlePrefix = "Smoke Scale Film";
    private const string SmokeLibraryName = "Smoke Scale Library";

    /// <summary>
    /// The five states <see cref="WantedStatuses"/> can hold that a synthetic
    /// title can be given directly, spread evenly. <c>Airing</c> is a TV-only
    /// state (WantedStatuses's own doc comment), so it is not one of these.
    /// </summary>
    private static readonly string[] Buckets =
    [
        WantedStatuses.Missing,
        WantedStatuses.Upgrade,
        WantedStatuses.Covered,
        WantedStatuses.Downloading,
        WantedStatuses.Upcoming
    ];

    public static IEndpointRouteBuilder MapDelunoMoviesLargeLibrarySeedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/testing/movies/seed-large-library", async (
            HttpContext httpContext,
            [FromBody] SeedLargeMovieLibraryRequest? request,
            IMovieCatalogRepository repository,
            IMediaStateRepository mediaState,
            ILibrariesRepository librariesRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // Clamped rather than trusted: this bypasses the normal add path's
            // validation, and a caller-supplied count feeds a loop directly.
            var count = Math.Clamp(request?.Count ?? 3000, 5, 20_000);
            var libraryId = await EnsureMovieLibraryAsync(librariesRepository, cancellationToken);
            var now = timeProvider.GetUtcNow();

            var perBucket = Buckets.ToDictionary(bucket => bucket, _ => 0);
            // #1048: every status pill counts monitored titles only (an
            // unmonitored title is under Unmonitored and nowhere else), so each
            // chip's own facet is the monitored half of its bucket. The smoke
            // specs need that split to assert the count correctly rather than
            // re-deriving the alternation pattern below by hand.
            var perBucketMonitored = Buckets.ToDictionary(bucket => bucket, _ => 0);
            var monitoredCount = 0;
            var unmonitoredCount = 0;

            for (var index = 0; index < count; index += 1)
            {
                var bucket = Buckets[index % Buckets.Length];
                // Alternated rather than randomised: every bucket ends up with
                // both a monitored and an unmonitored title in it, which is
                // what lets the smoke spec check "missing" and "unmonitored"
                // together without a separate seeding pass.
                var monitored = index % 2 == 0;
                var releaseYear = bucket == WantedStatuses.Upcoming
                    ? now.Year + 1 + (index % 3)
                    : 1970 + (index % 55);

                var movie = await repository.AddAsync(
                    new CreateMovieRequest(
                        Title: $"{TitlePrefix} {bucket} {index:D5}",
                        ReleaseYear: releaseYear,
                        ImdbId: null,
                        Monitored: monitored,
                        LibraryId: libraryId),
                    cancellationToken);

                // Covered and Upgrade are the two rungs a title with a file can
                // be on (library-filters.ts's own comment on QuickFilter); the
                // rest have none. Quality met only for Covered — the catalogue
                // counts every chip from the stored wanted_status rung (#1047),
                // and V0045's triggers reject a row whose flags disagree with
                // it, so the flags are written to agree with the bucket.
                var hasFile = bucket is WantedStatuses.Covered or WantedStatuses.Upgrade;
                var qualityCutoffMet = bucket == WantedStatuses.Covered;

                await repository.EnsureWantedStateAsync(
                    movie.Id,
                    libraryId,
                    bucket,
                    wantedReason: "smoke-large-library-seed",
                    hasFile: hasFile,
                    currentQuality: hasFile ? "WEBDL-1080p" : null,
                    targetQuality: "WEBDL-1080p",
                    qualityCutoffMet: qualityCutoffMet,
                    cancellationToken);

                if (bucket == WantedStatuses.Downloading)
                {
                    // Sets downloading_since_utc too, which is what the
                    // Downloading facet and the stuck-download sweep both
                    // read — EnsureWantedStateAsync alone leaves it null.
                    await mediaState.SetDownloadingAsync(
                        MediaKind.Movie,
                        movie.Id,
                        downloading: true,
                        now,
                        cancellationToken);
                }

                perBucket[bucket] += 1;
                if (monitored)
                {
                    monitoredCount += 1;
                    perBucketMonitored[bucket] += 1;
                }
                else
                {
                    unmonitoredCount += 1;
                }
            }

            return Results.Ok(new SeedLargeMovieLibraryResult(
                TotalCreated: count,
                LibraryId: libraryId,
                TitlePrefix: TitlePrefix,
                PerBucket: perBucket,
                PerBucketMonitored: perBucketMonitored,
                MonitoredCount: monitoredCount,
                UnmonitoredCount: unmonitoredCount));
        });

        // The other half of the seam: a smoke run's database is a fresh temp
        // directory (playwright.config.ts's `smokeDataRoot`), so nothing here
        // survives to the *next* run — but every spec after this one within
        // the *same* run shares that one database and one worker (see this
        // config's own comment on why), so three thousand extra titles left
        // behind would change what "the dashboard has nothing yet" or "Add a
        // movie" means for every spec that runs later. This is what keeps the
        // large-library spec's own footprint to itself.
        //
        // Deletes at the same repository level the seed used — no
        // TitleRemovalService, so no per-title activity-feed row or file
        // delete attempt for three thousand titles that were never really
        // downloaded.
        endpoints.MapPost("/api/testing/movies/unseed-large-library", async (
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            ILibrariesRepository librariesRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var all = await repository.ListAsync(cancellationToken);
            var seeded = all.Where(movie => movie.Title.StartsWith(TitlePrefix, StringComparison.Ordinal)).ToArray();

            var removed = 0;
            foreach (var movie in seeded)
            {
                if (await repository.DeleteAsync(movie.Id, cancellationToken))
                {
                    removed += 1;
                }
            }

            // Only ever removes the library this same endpoint created —
            // never a library a person (or another spec) made, which nothing
            // here names "Smoke Scale Library".
            var libraryRemoved = false;
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var smokeLibrary = libraries.FirstOrDefault(library =>
                string.Equals(library.Name, SmokeLibraryName, StringComparison.Ordinal) &&
                string.Equals(library.MediaType, "movies", StringComparison.OrdinalIgnoreCase));
            if (smokeLibrary is not null)
            {
                libraryRemoved = await librariesRepository.DeleteLibraryAsync(smokeLibrary.Id, cancellationToken);
            }

            return Results.Ok(new UnseedLargeMovieLibraryResult(removed, libraryRemoved));
        });

        return endpoints;
    }

    /// <summary>
    /// The library every seeded title is filed under. Reuses the first movies
    /// library already on record — the same "one library of the type" default
    /// <see cref="LibraryMembership.Targets"/> gives the real add path — and
    /// creates a bare one only when a fresh smoke database has none yet, so
    /// this endpoint works whether it runs before or after the specs that
    /// exercise onboarding.
    /// </summary>
    private static async Task<string> EnsureMovieLibraryAsync(
        ILibrariesRepository librariesRepository,
        CancellationToken cancellationToken)
    {
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var existing = libraries.FirstOrDefault(library =>
            string.Equals(library.MediaType, "movies", StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.Id;
        }

        var created = await librariesRepository.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: SmokeLibraryName,
                MediaType: "movies",
                Purpose: "General",
                RootPath: "/smoke/movies",
                DownloadsPath: null,
                QualityProfileId: null,
                ImportWorkflow: null,
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null,
                ProcessorFailureMode: null,
                AutoSearchEnabled: false,
                MissingSearchEnabled: false,
                UpgradeSearchEnabled: false,
                SearchIntervalMinutes: null,
                RetryDelayHours: null,
                MaxItemsPerRun: null),
            cancellationToken);
        return created.Id;
    }
}

public sealed record SeedLargeMovieLibraryRequest(int? Count);

public sealed record SeedLargeMovieLibraryResult(
    int TotalCreated,
    string LibraryId,
    string TitlePrefix,
    IReadOnlyDictionary<string, int> PerBucket,
    IReadOnlyDictionary<string, int> PerBucketMonitored,
    int MonitoredCount,
    int UnmonitoredCount);

public sealed record UnseedLargeMovieLibraryResult(int Removed, bool LibraryRemoved);

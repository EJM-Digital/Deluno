using System.Globalization;
using System.Text.Json;
using Deluno.Integrations.Metadata;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;

namespace Deluno.Movies.Services;

public interface ILibraryCollectionsService
{
    Task<LibraryCollectionsResponse> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds every film of a collection that the library lacks (#1425): follows the collection, monitored and adding its
    /// missing films, then runs the normal collection sync that adds them. Null when the provider does not know the collection.
    /// </summary>
    Task<AddMissingCollectionFilmsResult?> AddMissingAsync(string providerId, string? libraryId, CancellationToken cancellationToken);
}

/// <summary>
/// Radarr's Collections tab for Deluno (#1425): the collections the films already held belong to, each member marked held,
/// wanted or missing, and the ones that are close to complete suggested first. The collection a film belongs to is read from
/// the film's own stored metadata; the members come from the collection Deluno follows, or else from the metadata broker.
/// </summary>
public sealed class LibraryCollectionsService(
    IMovieCatalogRepository movies,
    IMovieCollectionsRepository followed,
    IMovieCollectionService collectionService,
    IMetadataProvider metadataProvider,
    ILibrariesRepository libraries) : ILibraryCollectionsService
{
    private const int MostRecommended = 8;

    public async Task<LibraryCollectionsResponse> ListAsync(CancellationToken cancellationToken)
    {
        var all = await movies.ListAsync(cancellationToken);
        // The list leaves each film's stored metadata out; the collection a film belongs to is read from just these.
        var metadata = await movies.ListCollectionMetadataAsync(cancellationToken);
        var following = (await followed.ListAsync(cancellationToken))
            .ToDictionary(item => item.ProviderId, StringComparer.OrdinalIgnoreCase);

        var groups = all
            .Select(movie => (Movie: movie, Collection: ReadCollection(metadata.GetValueOrDefault(movie.Id))))
            .Where(pair => pair.Collection is not null)
            .GroupBy(pair => pair.Collection!.Value.ProviderId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().Collection!.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var byProviderId = all
            .Where(movie => !string.IsNullOrWhiteSpace(movie.MetadataProviderId))
            .GroupBy(movie => movie.MetadataProviderId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var collections = new List<LibraryCollectionItem>(groups.Length);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var providerId = group.Key;
            var name = group.First().Collection!.Value.Name;
            var held = group.Select(pair => pair.Movie).ToArray();
            following.TryGetValue(providerId, out var follow);
            collections.Add(await BuildAsync(providerId, name, held, follow, byProviderId, cancellationToken));
        }

        var recommended = collections
            .Where(item => !item.Following && item.MembersKnown && item.MissingCount > 0 && item.HeldCount + item.WantedCount > 0)
            .OrderBy(item => item.MissingCount == 1 ? 0 : 1)
            .ThenByDescending(item => (double)(item.HeldCount + item.WantedCount) / Math.Max(1, item.Members.Count(member => !member.IsExcluded)))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MostRecommended)
            .Select(item => item with { Why = WhyRecommended(item) })
            .ToArray();

        return new LibraryCollectionsResponse(collections, recommended);
    }

    private async Task<LibraryCollectionItem> BuildAsync(
        string providerId,
        string name,
        IReadOnlyList<Contracts.MovieListItem> heldFilms,
        MovieCollectionItem? follow,
        IReadOnlyDictionary<string, Contracts.MovieListItem> byProviderId,
        CancellationToken cancellationToken)
    {
        var members = new List<LibraryCollectionMember>();
        var known = false;
        string? overview = follow?.Overview;
        string? poster = follow?.PosterUrl;

        if (follow is not null)
        {
            foreach (var member in await followed.ListMembersAsync(follow.Id, cancellationToken))
            {
                members.Add(Member(member.ProviderId, member.Title, member.ReleaseYear, member.PosterUrl, member.LocalMovieId, member.IsExcluded, byProviderId, heldFilms));
            }

            known = members.Count > 0;
        }
        else
        {
            MetadataCollection? metadata = null;
            try
            {
                metadata = await metadataProvider.GetMovieCollectionAsync(providerId, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // The broker not answering is not a failure of this page: the collection lists the films the library holds.
            }

            if (metadata is { Movies.Count: > 0 })
            {
                known = true;
                overview = metadata.Overview;
                poster = metadata.PosterUrl;
                name = string.IsNullOrWhiteSpace(metadata.Name) ? name : metadata.Name;
                foreach (var film in metadata.Movies)
                {
                    members.Add(Member(film.ProviderId, film.Title, film.Year, film.PosterUrl, null, false, byProviderId, heldFilms));
                }
            }
        }

        if (!known)
        {
            // The member list is not known: list the films the library holds, so the collection is still there to see.
            members.Clear();
            foreach (var film in heldFilms)
            {
                members.Add(Member(film.MetadataProviderId ?? film.Id, film.Title, film.ReleaseYear, film.PosterUrl, film.Id, false, byProviderId, heldFilms));
            }
        }

        poster ??= heldFilms.Select(film => film.PosterUrl).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        var counted = members.Where(member => !member.IsExcluded).ToArray();
        return new LibraryCollectionItem(
            providerId,
            name,
            overview,
            poster,
            follow?.Id,
            follow is not null,
            counted.Count(member => member.Status == LibraryCollectionStatuses.Held),
            counted.Count(member => member.Status == LibraryCollectionStatuses.Wanted),
            known ? counted.Count(member => member.Status == LibraryCollectionStatuses.Missing) : 0,
            members,
            known);
    }

    private static LibraryCollectionMember Member(
        string providerId,
        string title,
        int? year,
        string? posterUrl,
        string? localMovieId,
        bool excluded,
        IReadOnlyDictionary<string, Contracts.MovieListItem> byProviderId,
        IReadOnlyList<Contracts.MovieListItem> heldFilms)
    {
        var local = localMovieId is not null
            ? heldFilms.FirstOrDefault(movie => movie.Id == localMovieId)
            : null;
        local ??= byProviderId.GetValueOrDefault(providerId);
        var status = local is null ? LibraryCollectionStatuses.Missing
            : local.HasFile ? LibraryCollectionStatuses.Held
            : local.Monitored ? LibraryCollectionStatuses.Wanted
            : LibraryCollectionStatuses.Missing;
        return new LibraryCollectionMember(providerId, title, year, posterUrl ?? local?.PosterUrl, status, local?.Id ?? localMovieId, excluded);
    }

    internal static string WhyRecommended(LibraryCollectionItem item)
    {
        var inLibrary = item.HeldCount + item.WantedCount;
        var total = item.Members.Count(member => !member.IsExcluded);
        if (item.MissingCount == 1)
        {
            return $"You have {inLibrary} of {total} films. One more completes {item.Name}.";
        }

        if (inLibrary * 2 >= total)
        {
            return $"You have {inLibrary} of {total} films, so {item.MissingCount} more would complete {item.Name}.";
        }

        var first = item.Members.FirstOrDefault(member => member.Status != LibraryCollectionStatuses.Missing)?.Title ?? item.Name;
        return $"{first} is in your library, and {item.MissingCount} more films of {item.Name} are not.";
    }

    public async Task<AddMissingCollectionFilmsResult?> AddMissingAsync(string providerId, string? libraryId, CancellationToken cancellationToken)
    {
        var movieLibraries = (await libraries.ListLibrariesAsync(cancellationToken))
            .Where(library => string.Equals(library.MediaType, "movies", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var existing = (await followed.ListAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

        var wanted = !string.IsNullOrWhiteSpace(libraryId) ? libraryId.Trim() : existing?.LibraryId;
        var library = wanted is not null
            ? movieLibraries.FirstOrDefault(item => string.Equals(item.Id, wanted, StringComparison.OrdinalIgnoreCase))
            : movieLibraries.FirstOrDefault();
        if (library is null)
        {
            throw new ArgumentException(movieLibraries.Length == 0
                ? "Create a movie library first, so Deluno knows where the missing films go."
                : "Choose a movie library for the missing films.");
        }

        MovieCollectionItem? collection;
        if (existing is null)
        {
            collection = await collectionService.CreateOrUpdateAsync(
                new CreateMovieCollectionRequest(providerId, library.Id, Monitored: true, MonitorMovies: true, SearchOnAdd: true),
                cancellationToken);
            if (collection is null)
            {
                return null;
            }
        }
        else
        {
            collection = existing.Monitored && existing.MonitorMovies
                ? existing
                : await followed.UpdateAsync(existing.Id, new UpdateMovieCollectionRequest(Monitored: true, MonitorMovies: true), cancellationToken) ?? existing;
        }

        var sync = await collectionService.SyncAsync(collection.Id, cancellationToken);
        return new AddMissingCollectionFilmsResult(collection.Id, collection.Name, sync.AddedCount, sync.AddedCount > 0
            ? $"Added {sync.AddedCount} {(sync.AddedCount == 1 ? "film" : "films")} of {collection.Name}, and Deluno now follows the collection."
            : $"Nothing was missing from {collection.Name}. Deluno now follows the collection.", sync.SearchRequested);
    }

    /// <summary>The collection a film's stored metadata says it belongs to: its provider id and name.</summary>
    internal static (string ProviderId, string Name)? ReadCollection(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var id = Text(document.RootElement, "CollectionProviderId", "collectionProviderId", "collectionId");
            if (id is null || !id.All(char.IsAsciiDigit))
            {
                return null;
            }

            return (id, Text(document.RootElement, "Collection", "collection", "collectionName") ?? $"Collection {id}");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }

        return null;
    }
}

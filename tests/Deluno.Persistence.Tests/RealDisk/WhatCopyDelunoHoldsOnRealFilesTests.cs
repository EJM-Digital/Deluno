using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// What a film's copy is, in words, over HTTP with a real file on a real disk and real database files: the answer comes from
/// the file's own kept record first (even when a newer send of the same film says something else), falls back to the newest
/// archived send for a copy kept before that record existed, and an unknown film is not found.
/// </summary>
public sealed class WhatCopyDelunoHoldsOnRealFilesTests
{
    private const string Release = "Sintel.2010.WEB.1080p-DELUNO";

    [Fact]
    public async Task The_files_own_record_answers_first_even_when_a_newer_send_of_the_same_film_says_something_else()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var root = await ApiScenes.LibraryRootAsync(app, libraryId);
        var file = Path.Combine(root, "Sintel (2010)", "Sintel (2010) [WEB-1080p].mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        var grabbed = DateTimeOffset.Parse("2026-10-01T08:00:00Z");
        var snapshot = new PreferenceEvaluationSnapshot(
            string.Empty, libraryId, "preference-file/v1:sintel", file, 4, "quality-profile/own", "1", "0123456789abcdef", [],
            new PreferenceEvaluation("quality-profile/own", "1", "0123456789abcdef", PreferenceEvaluationStatus.MeetsPlan, hardGatesPassed: true, targetsMet: true, families: [], reasons: []),
            [], DateTimeOffset.Parse("2026-10-01T09:00:00Z"), "library-import", Release,
            new ReleaseAcquisition("Own Record Indexer", "Own Record Client", "hash-1", grabbed, null));
        var movies = app.Services.GetRequiredService<IMovieCatalogRepository>();
        await movies.ImportExistingBatchAsync(
            libraryId,
            [new ExistingMovieImportRequest("Sintel", 2010, WantedStatuses.Covered, "Imported.", "WEB-1080p", "WEB-1080p", QualityCutoffMet: true, file, 4, snapshot)],
            CancellationToken.None);
        var movieId = (await movies.ListAsync(CancellationToken.None)).Single().Id;
        // A later send of the same film from somewhere else: the file's own record still wins.
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            libraryId, "movies", "movie", movieId, "Another.Copy.2010.2160p", "Newer Indexer", "client-9", "Newer Client", "sent", null, cancellationToken: CancellationToken.None);

        var response = await app.Client.GetAsync($"/api/movies/{movieId}/copy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var copy = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(movieId, copy.GetProperty("movieId").GetString());
        Assert.Equal(file, copy.GetProperty("filePath").GetString());
        Assert.Equal(Release, copy.GetProperty("releaseName").GetString());
        Assert.Equal("Own Record Indexer", copy.GetProperty("indexerName").GetString());
        Assert.Equal("Own Record Client", copy.GetProperty("downloadClientName").GetString());
        Assert.Equal(grabbed, copy.GetProperty("grabbedUtc").GetDateTimeOffset());
        Assert.Equal("library-import", copy.GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_copy_kept_before_the_files_own_record_existed_is_answered_from_the_newest_archived_send()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var film = await ApiScenes.FilmOnDiskAsync(app, "Sintel", 2010);
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var older = await jobs.RecordDownloadDispatchAsync(
            film.LibraryId, "movies", "movie", film.MovieId, "Older.Copy.2010.720p", "Older Indexer", "client-1", "Older Client", "sent", null, cancellationToken: CancellationToken.None);
        await Wait.ClockMovesOnAsync();
        var newest = await jobs.RecordDownloadDispatchAsync(
            film.LibraryId, "movies", "movie", film.MovieId, Release, "Newest Indexer", "client-2", "Newest Client", "sent", null, cancellationToken: CancellationToken.None);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        await dispatches.ArchiveDispatchAsync(older, "imported", CancellationToken.None);
        await dispatches.ArchiveDispatchAsync(newest, "imported", CancellationToken.None);

        var copy = await app.Client.GetFromJsonAsync<JsonElement>($"/api/movies/{film.MovieId}/copy");

        Assert.Equal("Newest Indexer", copy.GetProperty("indexerName").GetString());
        Assert.Equal("Newest Client", copy.GetProperty("downloadClientName").GetString());
        Assert.Equal(Release, copy.GetProperty("releaseName").GetString());
        Assert.Equal(JsonValueKind.Null, copy.GetProperty("importedUtc").ValueKind);
    }

    [Fact]
    public async Task A_film_Deluno_does_not_know_is_not_found()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/movies/no-such-film/copy")).StatusCode);
    }
}

using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1098: a seeding clean-up (retention removing a finished torrent once it had shared enough) is said in plain
/// words in Activity and the row is pushed like any other activity, so the Live screen's Activity list moves.
/// </summary>
public sealed class SeedingCleanupActivityTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-10-01T09:00:00Z"));
    private readonly RecordingRealtimeEventPublisher _realtime = new();
    private readonly SqliteJobStore _jobs;

    public SeedingCleanupActivityTests()
    {
        new JobsSchemaInitializer(_storage.Factory, new SqliteDatabaseMigrator(_storage.Factory, _time), NullLogger<JobsSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var dispatches = new SqliteDownloadDispatchesRepository(_storage.Factory, _time, _realtime);
        _jobs = new SqliteJobStore(_storage.Factory, _time, _realtime, dispatches);
    }

    public void Dispose() => _storage.Dispose();

    [Fact]
    public void The_sentence_names_the_title_and_the_client_and_says_the_library_copy_stays()
        => Assert.Equal(
            "Removed Sintel (2010) from qBittorrent once it had shared enough · your copy stays in the library",
            SeedingCleanupActivity.Message("Sintel (2010)", "qBittorrent"));

    [Fact]
    public void The_client_is_called_by_its_own_short_name_never_with_the_machine_it_runs_on()
        => Assert.Equal(
            "Removed Big Buck Bunny (2008) from Deluge once it had shared enough · your copy stays in the library",
            SeedingCleanupActivity.Message("Big Buck Bunny (2008)", "Deluge on MEDIA-SERVER"));

    [Theory]
    [InlineData("Big Buck Bunny", 2008, "Big Buck Bunny (2008)")]
    [InlineData("Big Buck Bunny (2008)", 2008, "Big Buck Bunny (2008)")]
    [InlineData("Lioness", null, "Lioness")]
    public void The_library_title_carries_its_year_once(string title, int? year, string expected)
        => Assert.Equal(expected, SeedingCleanupActivity.LibraryTitle(title, year));

    [Fact]
    public async Task The_row_is_stored_under_the_category_the_live_screen_counts_and_is_pushed()
    {
        var row = await _jobs.RecordActivityOnChangeAsync(
            SeedingCleanupActivity.Category,
            SeedingCleanupActivity.Message("Sintel (2010)", "qBittorrent"),
            null, null, "movie", "m1", CancellationToken.None);

        Assert.NotNull(row);
        Assert.Equal("download.sharing.reclaimed", row!.Category);
        Assert.Contains(_realtime.ActivityEvents, activity => activity.Category == "download.sharing.reclaimed");
    }
}

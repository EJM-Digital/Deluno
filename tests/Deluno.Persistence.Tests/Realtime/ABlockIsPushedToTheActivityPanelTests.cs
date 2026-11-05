using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>
/// #1491: the Dashboard's Activity panel is live. Deluno's own block of a release is a row of the history
/// (the line after a strike on the rig), and it was written without being pushed, so the panel showed the
/// replacement grab but never the block until a reload. It is pushed when written, once.
/// </summary>
public sealed class ABlockIsPushedToTheActivityPanelTests : IDisposable
{
    private readonly TestStorage _storage = TestStorage.Create();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-11-05T04:30:00Z"));
    private readonly RecordingRealtimeEventPublisher _realtime = new();
    private readonly SqliteBlockedReleaseRepository _blocked;

    public ABlockIsPushedToTheActivityPanelTests()
    {
        new JobsSchemaInitializer(_storage.Factory, new SqliteDatabaseMigrator(_storage.Factory, _time), NullLogger<JobsSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _blocked = new SqliteBlockedReleaseRepository(_storage.Factory, _time, _realtime);
    }

    public void Dispose() => _storage.Dispose();

    private static BlockedRelease Release(string name, string origin = BlockedReleaseOrigins.Automatic)
        => new("b-" + name, "key-" + name, name, "Idx", "movies", "movie-barbie", "Barbie", "tooSlow", "Too slow",
            "hash1", "dc1", "qBittorrent", default, BlockedReleaseStates.Refused, origin);

    [Fact]
    public async Task Deluno_blocking_a_release_pushes_one_activity_row_as_it_is_written()
    {
        await _blocked.BlockAsync(Release("Barbie.2023.BluRay.1080p"), CancellationToken.None);

        var pushed = Assert.Single(_realtime.ActivityEvents);
        Assert.Equal("release.blocked", pushed.Category);
    }

    [Fact]
    public async Task The_same_release_blocked_again_is_not_pushed_again()
    {
        await _blocked.BlockAsync(Release("Barbie.2023.BluRay.1080p"), CancellationToken.None);
        await _blocked.BlockAsync(Release("Barbie.2023.BluRay.1080p"), CancellationToken.None);

        Assert.Single(_realtime.ActivityEvents);
    }

    [Fact]
    public async Task A_block_the_person_made_writes_its_own_line_and_is_not_pushed_here()
    {
        await _blocked.BlockAsync(Release("Barbie.2023.BluRay.1080p", BlockedReleaseOrigins.Manual), CancellationToken.None);

        Assert.Empty(_realtime.ActivityEvents);
    }

    /// <summary>
    /// Every category Deluno labels goes through the one writer, and the one writer pushes: no category is written
    /// quietly, so the panel can show whatever its table says is shown.
    /// </summary>
    [Fact]
    public async Task Every_labelled_category_recorded_through_the_writer_is_pushed()
    {
        var jobs = new SqliteJobStore(_storage.Factory, _time, _realtime, new NullDownloadDispatchesRepository());

        foreach (var category in ActivityEventLabels.KnownCategories)
        {
            await jobs.RecordActivityAsync(category, "Something happened to a title", null, null, "movie", "movie-barbie", CancellationToken.None);
        }

        Assert.Equal(
            ActivityEventLabels.KnownCategories.Order(StringComparer.OrdinalIgnoreCase),
            _realtime.ActivityEvents.Select(item => item.Category).Order(StringComparer.OrdinalIgnoreCase));
    }
}

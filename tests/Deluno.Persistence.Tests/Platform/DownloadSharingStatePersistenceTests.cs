using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>
/// What the dashboard reads to answer "why is my drive full" (#288).
///
/// The worker writes this picture every sharing pass and the dashboard renders
/// it verbatim, so the two things that matter are that the sentence survives
/// the round trip unedited, and that a picture nobody has refreshed stops being
/// shown rather than quietly ageing into a lie.
/// </summary>
public sealed class DownloadSharingStatePersistenceTests
{
    private static async Task<(SqliteDownloadSharingRepository Repository, FixedTimeProvider Clock)> CreateAsync()
    {
        var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-26T02:00:00Z"));

        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return (new SqliteDownloadSharingRepository(storage.Factory, clock), clock);
    }

    private static DownloadSharingHold Hold(
        string title = "Sintel (2010)",
        string detail = "2 days left",
        long sizeBytes = 12_884_901_888,
        bool needsYou = false,
        bool sharesLibraryCopy = false)
        => new("client-1", "qBittorrent", "hash-1", title, detail, sizeBytes, needsYou, sharesLibraryCopy);

    [Fact]
    public async Task An_install_that_has_never_run_a_pass_holds_nothing()
    {
        var (repository, _) = await CreateAsync();

        var snapshot = await repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Empty(snapshot.Holds);
        Assert.Equal(0, snapshot.ExtraBytes);
        Assert.Null(snapshot.DriveNote);
        Assert.Null(snapshot.ObservedUtc);
    }

    [Fact]
    public async Task The_evaluator_wording_comes_back_exactly_as_it_was_written()
    {
        var (repository, clock) = await CreateAsync();

        await repository.ReplaceHoldsAsync([Hold()], "Your downloads land on C: and your library is on D:.", CancellationToken.None);
        var snapshot = await repository.GetSnapshotAsync(CancellationToken.None);

        var hold = Assert.Single(snapshot.Holds);
        Assert.Equal("Sintel (2010)", hold.Title);
        Assert.Equal("2 days left", hold.Detail);
        Assert.Equal(12_884_901_888, hold.SizeBytes);
        Assert.Equal("Your downloads land on C: and your library is on D:.", snapshot.DriveNote);
        Assert.Equal(clock.GetUtcNow(), snapshot.ObservedUtc);
    }

    /// <summary>
    /// Only a copy that is genuinely separate is charged for. A hardlinked
    /// download and its library file are one set of bytes, and billing the user
    /// twice for them would send them deleting things they did not need to.
    /// </summary>
    [Fact]
    public async Task Space_is_only_counted_where_the_copy_is_a_second_one()
    {
        var (repository, _) = await CreateAsync();

        await repository.ReplaceHoldsAsync(
            [
                Hold(title: "Sintel (2010)", sizeBytes: 4_000, sharesLibraryCopy: false),
                Hold(title: "Big Buck Bunny (2008)", sizeBytes: 9_000, sharesLibraryCopy: true)
            ],
            driveNote: null,
            CancellationToken.None);

        var snapshot = await repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Holds.Count);
        Assert.Equal(4_000, snapshot.ExtraBytes);
    }

    [Fact]
    public async Task Each_pass_replaces_the_picture_rather_than_adding_to_it()
    {
        var (repository, _) = await CreateAsync();

        await repository.ReplaceHoldsAsync([Hold(title: "Sintel (2010)"), Hold(title: "Tears of Steel (2012)")], null, CancellationToken.None);
        await repository.ReplaceHoldsAsync([Hold(title: "Sintel (2010)")], null, CancellationToken.None);

        var snapshot = await repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("Sintel (2010)", Assert.Single(snapshot.Holds).Title);
    }

    [Fact]
    public async Task A_blank_drive_note_is_no_note_at_all()
    {
        var (repository, _) = await CreateAsync();

        await repository.ReplaceHoldsAsync([Hold()], "   ", CancellationToken.None);

        Assert.Null((await repository.GetSnapshotAsync(CancellationToken.None)).DriveNote);
    }

    /// <summary>
    /// The pass runs every thirty seconds. Anything older than the freshness
    /// window means the worker is not running, and a dashboard confidently
    /// reporting yesterday's disk usage is worse than one that says nothing.
    /// </summary>
    [Fact]
    public async Task A_picture_nobody_has_refreshed_stops_being_shown()
    {
        var (repository, clock) = await CreateAsync();

        await repository.ReplaceHoldsAsync([Hold()], "on different drives", CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Single((await repository.GetSnapshotAsync(CancellationToken.None)).Holds);

        clock.Advance(TimeSpan.FromMinutes(2));
        var stale = await repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Empty(stale.Holds);
        Assert.Null(stale.DriveNote);
        Assert.Null(stale.ObservedUtc);
    }

    [Fact]
    public async Task A_pass_that_finds_nothing_clears_what_the_last_one_found()
    {
        var (repository, _) = await CreateAsync();

        await repository.ReplaceHoldsAsync([Hold()], "on different drives", CancellationToken.None);
        await repository.ReplaceHoldsAsync([], null, CancellationToken.None);

        var snapshot = await repository.GetSnapshotAsync(CancellationToken.None);

        Assert.Empty(snapshot.Holds);
        Assert.Equal(0, snapshot.ExtraBytes);
        Assert.Null(snapshot.DriveNote);
    }
}

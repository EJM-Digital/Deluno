using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Subtitles.Providers;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Subtitles;

/// <summary>
/// #1301: the file a person ends up with. The subtitle is the one Subf2m really sent (tests/fixtures/recorded/subf2m/sintel-english, a real
/// zip) and the real writer puts it in a real temporary folder next to the video: its name, that a forced track is a second file that never
/// replaces the full one, and that fetching the full one again replaces only the full one. Subf2m lists no forced track, so the forced tests
/// ask the same recorded provider with its candidates marked forced (the nearest recorded reply, changed in the one value the test is about).
/// </summary>
public sealed class SubtitleWrittenOnARecordedSubf2mArchiveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-sub-" + Guid.NewGuid().ToString("N"));
    private readonly string _video;

    public SubtitleWrittenOnARecordedSubf2mArchiveTests()
    {
        Directory.CreateDirectory(_folder);
        _video = Path.Combine(_folder, "Sintel (2010).mkv");
        File.WriteAllBytes(_video, [0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static SubtitleSearchRequest Film() => new("Sintel", 2010, null, null, null, null, null, ["en"], IsEpisode: false);

    private static SubtitleFileWriter RealWriter()
    {
        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        return new SubtitleFileWriter(NullLogger<SubtitleFileWriter>.Instance, settings.Object);
    }

    private static PlatformSettingsSnapshot Snapshot()
        => new(
            MovieRootPath: null, SeriesRootPath: null, DownloadsPath: null, IncompleteDownloadsPath: null,
            AutoStartJobs: true, EnableNotifications: true, RenameOnImport: true, UseHardlinks: false, CleanupEmptyFolders: true,
            MovieFolderFormat: "{Movie}", SeriesFolderFormat: "{Series}", MovieFileFormat: "{Movie}", EpisodeFileFormat: "{Episode}",
            HostBindAddress: "127.0.0.1", HostPort: 5099, UrlBase: string.Empty, RequireAuthentication: true, UiTheme: "system",
            DefaultMovieView: "grid", DefaultShowView: "grid", MetadataCertificationCountry: "US", MetadataLanguage: "en",
            MetadataProviderMode: "auto", MetadataBrokerUrl: string.Empty, MetadataBrokerConfigured: false,
            MetadataTmdbApiKeyConfigured: false, MetadataOmdbApiKeyConfigured: false, ReleaseNeverGrabPatterns: string.Empty,
            SearchScoringMode: "balanced", ImportRecoveryRetentionDays: 30, UpdatedUtc: DateTimeOffset.UnixEpoch);

    private static SubtitleFetchService Service(bool markForced)
    {
        var recorded = new KeylessSubtitleDownloadsOnRecordedRepliesTests.Recorded(RecordedReplies.Subf2m("sintel-english"));
        ISubtitleProvider provider = new Subf2mSubtitleProvider(new KeylessSubtitleDownloadsOnRecordedRepliesTests.Factory(recorded));
        if (markForced) provider = new ForcedMarking(provider);
        var row = new SubtitleProviderConnection("subf2m", "subf2m", "Subf2m", null, null, null, 100, true, "untested", null, null, null, 0, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return new SubtitleFetchService(new SubtitleProviderRegistry([provider]), new Repository([row]), RealWriter(), TimeProvider.System, NullLogger<SubtitleFetchService>.Instance);
    }

    [Fact]
    public async Task A_subtitle_Subf2m_really_sent_is_written_beside_the_video_named_after_it_and_its_language()
    {
        var outcome = await Service(false).FetchAsync(Film(), "en", false, _video, SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.True(outcome.Found, outcome.Reason);
        var written = Path.Combine(_folder, "Sintel (2010).en.srt");
        Assert.True(File.Exists(written));
        Assert.Contains("-->", await File.ReadAllTextAsync(written), StringComparison.Ordinal);
        Assert.Equal(["Sintel (2010).en.srt", "Sintel (2010).mkv"], Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public async Task A_forced_track_is_a_second_file_and_never_replaces_the_full_subtitle()
    {
        await Service(false).FetchAsync(Film(), "en", false, _video, SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);
        var full = Path.Combine(_folder, "Sintel (2010).en.srt");
        await File.AppendAllTextAsync(full, "\n"); // marks the full file so a replacement would show
        var marked = await File.ReadAllBytesAsync(full);

        var outcome = await Service(true).FetchForcedAsync(Film(), "en", false, _video, CancellationToken.None);

        Assert.True(outcome.Found, outcome.Reason);
        Assert.Equal(marked, await File.ReadAllBytesAsync(full));
        Assert.True(File.Exists(Path.Combine(_folder, "Sintel (2010).en.forced.srt")));
        Assert.Equal(3, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public async Task Fetching_the_full_subtitle_again_replaces_only_the_full_file_and_leaves_the_forced_one()
    {
        await Service(true).FetchForcedAsync(Film(), "en", false, _video, CancellationToken.None);
        var forced = Path.Combine(_folder, "Sintel (2010).en.forced.srt");
        await File.WriteAllTextAsync(forced, "kept");

        await Service(false).FetchAsync(Film(), "en", false, _video, SubtitleHearingImpairedPreferences.PlainPreferred, CancellationToken.None);

        Assert.Equal("kept", await File.ReadAllTextAsync(forced));
        Assert.Contains("-->", await File.ReadAllTextAsync(Path.Combine(_folder, "Sintel (2010).en.srt")), StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetFiles(_folder), file => file.EndsWith(".partial", StringComparison.Ordinal));
    }

    private sealed class ForcedMarking(ISubtitleProvider inner) : ISubtitleProvider
    {
        public string Key => inner.Key;
        public string DisplayName => inner.DisplayName;
        public string Description => inner.Description;
        public SubtitleProviderScope Scope => inner.Scope;
        public SubtitleCredentialFields RequiredCredentials => inner.RequiredCredentials;
        public bool CredentialsOptional => inner.CredentialsOptional;

        public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleSearchRequest request, SubtitleProviderCredentials credentials, CancellationToken cancellationToken)
            => (await inner.SearchAsync(request, credentials, cancellationToken)).Select(candidate => candidate with { Forced = true }).ToArray();

        public Task<byte[]> DownloadAsync(SubtitleCandidate candidate, SubtitleProviderCredentials credentials, CancellationToken cancellationToken)
            => inner.DownloadAsync(candidate, credentials, cancellationToken);
    }

    private sealed class Repository(IReadOnlyList<SubtitleProviderConnection> rows) : ISubtitleProviderRepository
    {
        public Task<IReadOnlyList<SubtitleProviderConnection>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(rows);

        public Task<SubtitleProviderConnection> SaveAsync(string providerKey, string displayName, SaveSubtitleProviderRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RecordHealthAsync(string providerKey, string status, string? message, int? latencyMs, bool success, DateTimeOffset? rateLimitedUntilUtc, CancellationToken cancellationToken, IntegrationFailure? failure = null)
            => Task.CompletedTask;

        public Task<bool> DeleteAsync(string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// Answers <see cref="GetAsync"/> with one fixed snapshot and refuses
/// everything else — the only shape a subtitle-writer test needs, since the
/// permissions setting is the only thing the writer reads off it (#766,
/// #838).
/// </summary>
public sealed class FakePlatformSettingsRepository(PlatformSettingsSnapshot snapshot) : IPlatformSettingsRepository
{
    public static FakePlatformSettingsRepository With(
        bool setPermissionsOnImport = false, string? folderChmod = null, string? chownGroup = null)
        => new(Default(setPermissionsOnImport, folderChmod, chownGroup));

    public static PlatformSettingsSnapshot Default(
        bool setPermissionsOnImport = false, string? folderChmod = null, string? chownGroup = null)
        => new(
            AppInstanceName: "Deluno",
            MovieRootPath: null,
            SeriesRootPath: null,
            DownloadsPath: null,
            IncompleteDownloadsPath: null,
            AutoStartJobs: true,
            EnableNotifications: true,
            RenameOnImport: true,
            UseHardlinks: true,
            CleanupEmptyFolders: true,
            MovieFolderFormat: "{Movie}",
            SeriesFolderFormat: "{Series}",
            MovieFileFormat: "{Movie}",
            EpisodeFileFormat: "{Episode}",
            HostBindAddress: "127.0.0.1",
            HostPort: 7979,
            UrlBase: string.Empty,
            RequireAuthentication: false,
            UiTheme: "dark",
            DefaultMovieView: "poster",
            DefaultShowView: "poster",
            MetadataArtworkEnabled: true,
            MetadataCertificationCountry: "US",
            MetadataLanguage: "en",
            MetadataProviderMode: "auto",
            MetadataBrokerUrl: string.Empty,
            MetadataBrokerConfigured: false,
            MetadataTmdbApiKeyConfigured: false,
            MetadataOmdbApiKeyConfigured: false,
            ReleaseNeverGrabPatterns: string.Empty,
            SearchScoringMode: "balanced",
            ImportRecoveryRetentionDays: 30,
            UpdatedUtc: DateTimeOffset.UnixEpoch,
            SetPermissionsOnImport: setPermissionsOnImport,
            FolderChmod: folderChmod,
            ChownGroup: chownGroup);

    public Task<PlatformSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
        => Task.FromResult(snapshot);

    public Task<SetupProgressItem> GetSetupProgressAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<SetupProgressItem> SaveSetupProgressAsync(UpdateSetupProgressRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<SetupDraftItem> GetSetupDraftAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<SetupDraftItem> SaveSetupDraftAsync(UpdateSetupDraftRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task ClearSetupDraftAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<string?> GetMetadataProviderSecretAsync(string provider, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<PlatformSettingsSnapshot> SaveAsync(UpdatePlatformSettingsRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<PlatformSettingsSnapshot> SetGlobalAutomationEnabledAsync(bool isEnabled, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<PlatformSettingsSnapshot> MarkWorkflowVerifiedAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<TagItem>> ListTagsAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<TagItem> CreateTagAsync(CreateTagRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<TagItem?> UpdateTagAsync(string id, UpdateTagRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<bool> DeleteTagAsync(string id, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

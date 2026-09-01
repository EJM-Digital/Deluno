namespace Deluno.Platform.Contracts;

public static class PlatformSettingsPatchMerger
{
    public static UpdatePlatformSettingsRequest Apply(
        PlatformSettingsSnapshot current,
        PatchPlatformSettingsRequest patch)
        => new(
            AppInstanceName: patch.AppInstanceName ?? current.AppInstanceName,
            MovieRootPath: patch.MovieRootPath ?? current.MovieRootPath,
            SeriesRootPath: patch.SeriesRootPath ?? current.SeriesRootPath,
            DownloadsPath: patch.DownloadsPath ?? current.DownloadsPath,
            IncompleteDownloadsPath: patch.IncompleteDownloadsPath ?? current.IncompleteDownloadsPath,
            AutoStartJobs: patch.AutoStartJobs ?? current.AutoStartJobs,
            EnableNotifications: patch.EnableNotifications ?? current.EnableNotifications,
            RenameOnImport: patch.RenameOnImport ?? current.RenameOnImport,
            UseHardlinks: patch.UseHardlinks ?? current.UseHardlinks,
            CleanupEmptyFolders: patch.CleanupEmptyFolders ?? current.CleanupEmptyFolders,
            RemoveCompletedDownloads: patch.RemoveCompletedDownloads ?? current.RemoveCompletedDownloads,
            UnmonitorWhenCutoffMet: patch.UnmonitorWhenCutoffMet ?? current.UnmonitorWhenCutoffMet,
            MovieFolderFormat: patch.MovieFolderFormat ?? current.MovieFolderFormat,
            SeriesFolderFormat: patch.SeriesFolderFormat ?? current.SeriesFolderFormat,
            EpisodeFileFormat: patch.EpisodeFileFormat ?? current.EpisodeFileFormat,
            HostBindAddress: patch.HostBindAddress ?? current.HostBindAddress,
            HostPort: patch.HostPort ?? current.HostPort,
            UrlBase: patch.UrlBase ?? current.UrlBase,
            RequireAuthentication: patch.RequireAuthentication ?? current.RequireAuthentication,
            UiTheme: patch.UiTheme ?? current.UiTheme,
            UiDensity: patch.UiDensity ?? current.UiDensity,
            DefaultMovieView: patch.DefaultMovieView ?? current.DefaultMovieView,
            DefaultShowView: patch.DefaultShowView ?? current.DefaultShowView,
            MetadataNfoEnabled: patch.MetadataNfoEnabled ?? current.MetadataNfoEnabled,
            MetadataArtworkEnabled: patch.MetadataArtworkEnabled ?? current.MetadataArtworkEnabled,
            MetadataCertificationCountry: patch.MetadataCertificationCountry ?? current.MetadataCertificationCountry,
            MetadataLanguage: patch.MetadataLanguage ?? current.MetadataLanguage,
            MetadataProviderMode: patch.MetadataProviderMode ?? current.MetadataProviderMode,
            MetadataBrokerUrl: patch.MetadataBrokerUrl ?? current.MetadataBrokerUrl,
            MetadataTmdbApiKey: patch.MetadataTmdbApiKey,
            MetadataOmdbApiKey: patch.MetadataOmdbApiKey,
            ReleaseNeverGrabPatterns: patch.ReleaseNeverGrabPatterns ?? current.ReleaseNeverGrabPatterns,
            SearchScoringMode: patch.SearchScoringMode ?? current.SearchScoringMode,
            ImportRecoveryRetentionDays: patch.ImportRecoveryRetentionDays ?? current.ImportRecoveryRetentionDays,
            MdbListApiKey: patch.MdbListApiKey,
            DownloadHealthStrikeThreshold: patch.DownloadHealthStrikeThreshold ?? current.DownloadHealthStrikeThreshold,
            CleanupBlockReleaseAfterThreshold: patch.CleanupBlockReleaseAfterThreshold ?? current.CleanupBlockReleaseAfterThreshold,
            CleanupQueueReplacementAfterThreshold: patch.CleanupQueueReplacementAfterThreshold ?? current.CleanupQueueReplacementAfterThreshold,
            CleanupRemoveClientEntryAfterThreshold: patch.CleanupRemoveClientEntryAfterThreshold ?? current.CleanupRemoveClientEntryAfterThreshold,
            CleanupPurgePayloadAfterThreshold: patch.CleanupPurgePayloadAfterThreshold ?? current.CleanupPurgePayloadAfterThreshold,
            SharingMode: patch.SharingMode ?? current.SharingMode,
            // The two targets carry meaning in their absence: a patch that sets
            // the mode is submitting the whole rule, so a missing target there
            // means "this half is not part of it" rather than "leave it as it
            // was". Only a patch that does not touch sharing at all inherits.
            SharingForHours: patch.SharingMode is null ? current.SharingForHours : patch.SharingForHours,
            SharingUntilRatio: patch.SharingMode is null ? current.SharingUntilRatio : patch.SharingUntilRatio,
            SharingStuckAction: patch.SharingStuckAction ?? current.SharingStuckAction,
            SharingStuckAfterDays: patch.SharingStuckAfterDays ?? current.SharingStuckAfterDays,
            UiColorMode: patch.UiColorMode ?? current.UiColorMode,
            UiLanguage: patch.UiLanguage ?? current.UiLanguage,
            CalendarFirstDayOfWeek: patch.CalendarFirstDayOfWeek ?? current.CalendarFirstDayOfWeek,
            CalendarWeekHeaderFormat: patch.CalendarWeekHeaderFormat ?? current.CalendarWeekHeaderFormat,
            RuntimeFormat: patch.RuntimeFormat ?? current.RuntimeFormat,
            ShortDateFormat: patch.ShortDateFormat ?? current.ShortDateFormat,
            LongDateFormat: patch.LongDateFormat ?? current.LongDateFormat,
            TimeFormat: patch.TimeFormat ?? current.TimeFormat,
            ShowRelativeDates: patch.ShowRelativeDates ?? current.ShowRelativeDates);
}

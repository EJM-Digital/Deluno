import { authedFetch } from "./use-auth";

export interface DatabaseDescriptor {
  key: string;
  fileName: string;
  purpose: string;
}

export interface ModuleDescriptor {
  name: string;
  purpose: string;
}

export interface SystemManifest {
  app: string;
  storageRoot: string;
  modules: ModuleDescriptor[];
  databases: DatabaseDescriptor[];
}

export interface MovieListItem {
  id: string;
  title: string;
  releaseYear: number | null;
  imdbId: string | null;
  monitored: boolean;
  metadataProvider: string | null;
  metadataProviderId: string | null;
  originalTitle: string | null;
  overview: string | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  rating: number | null;
  ratings?: MetadataRatingItem[] | null;
  genres: string | null;
  externalUrl: string | null;
  metadataJson: string | null;
  metadataUpdatedUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface MovieImportRecoveryCase {
  id: string;
  title: string;
  failureKind: string;
  summary: string;
  recommendedAction: string;
  detailsJson: string | null;
  detectedUtc: string;
}

export interface MovieImportRecoverySummary {
  openCount: number;
  qualityCount: number;
  unmatchedCount: number;
  corruptCount: number;
  downloadFailedCount: number;
  importFailedCount: number;
  recentCases: MovieImportRecoveryCase[];
}

export interface MovieWantedItem {
  movieId: string;
  title: string;
  releaseYear: number | null;
  imdbId: string | null;
  libraryId: string;
  wantedStatus: string;
  wantedReason: string;
  hasFile: boolean;
  currentQuality: string | null;
  targetQuality: string | null;
  qualityCutoffMet: boolean;
  missingSinceUtc: string | null;
  lastSearchUtc: string | null;
  nextEligibleSearchUtc: string | null;
  lastSearchResult: string | null;
  updatedUtc: string;
}

export interface MovieWantedSummary {
  totalWanted: number;
  missingCount: number;
  upgradeCount: number;
  waitingCount: number;
  recentItems: MovieWantedItem[];
}

export interface MovieSearchHistoryItem {
  id: string;
  movieId: string;
  libraryId: string;
  triggerKind: string;
  outcome: string;
  releaseName: string | null;
  indexerName: string | null;
  detailsJson: string | null;
  createdUtc: string;
}

export interface SeriesListItem {
  id: string;
  title: string;
  startYear: number | null;
  imdbId: string | null;
  monitored: boolean;
  metadataProvider: string | null;
  metadataProviderId: string | null;
  originalTitle: string | null;
  overview: string | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  rating: number | null;
  ratings?: MetadataRatingItem[] | null;
  genres: string | null;
  externalUrl: string | null;
  metadataJson: string | null;
  metadataUpdatedUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface SeriesImportRecoveryCase {
  id: string;
  title: string;
  failureKind: string;
  summary: string;
  recommendedAction: string;
  detailsJson: string | null;
  detectedUtc: string;
}

export interface SeriesImportRecoverySummary {
  openCount: number;
  qualityCount: number;
  unmatchedCount: number;
  corruptCount: number;
  downloadFailedCount: number;
  importFailedCount: number;
  recentCases: SeriesImportRecoveryCase[];
}

export interface SeriesWantedItem {
  seriesId: string;
  title: string;
  startYear: number | null;
  imdbId: string | null;
  libraryId: string;
  wantedStatus: string;
  wantedReason: string;
  hasFile: boolean;
  currentQuality: string | null;
  targetQuality: string | null;
  qualityCutoffMet: boolean;
  missingSinceUtc: string | null;
  lastSearchUtc: string | null;
  nextEligibleSearchUtc: string | null;
  lastSearchResult: string | null;
  updatedUtc: string;
}

export interface SeriesWantedSummary {
  totalWanted: number;
  missingCount: number;
  upgradeCount: number;
  waitingCount: number;
  recentItems: SeriesWantedItem[];
}

export interface SeriesInventorySummary {
  seriesCount: number;
  seasonCount: number;
  episodeCount: number;
  importedEpisodeCount: number;
}

export interface SeriesEpisodeInventoryItem {
  episodeId: string;
  seasonNumber: number;
  episodeNumber: number;
  title: string | null;
  airDateUtc: string | null;
  monitored: boolean;
  hasFile: boolean;
  wantedStatus: string;
  wantedReason: string;
  qualityCutoffMet: boolean;
  lastSearchUtc: string | null;
  nextEligibleSearchUtc: string | null;
  updatedUtc: string;
}

export interface SeriesInventoryDetail {
  seriesId: string;
  title: string;
  startYear: number | null;
  seasonCount: number;
  episodeCount: number;
  importedEpisodeCount: number;
  episodes: SeriesEpisodeInventoryItem[];
}

export interface SeriesSearchHistoryItem {
  id: string;
  seriesId: string;
  episodeId: string | null;
  seasonNumber: number | null;
  episodeNumber: number | null;
  libraryId: string;
  triggerKind: string;
  outcome: string;
  releaseName: string | null;
  indexerName: string | null;
  detailsJson: string | null;
  createdUtc: string;
}

export interface ValidationProblem {
  title?: string;
  errors?: Record<string, string[]>;
}

export interface PlatformSettingsSnapshot {
  appInstanceName: string;
  movieRootPath: string | null;
  seriesRootPath: string | null;
  downloadsPath: string | null;
  incompleteDownloadsPath: string | null;
  autoStartJobs: boolean;
  enableNotifications: boolean;
  renameOnImport: boolean;
  useHardlinks: boolean;
  cleanupEmptyFolders: boolean;
  removeCompletedDownloads: boolean;
  unmonitorWhenCutoffMet: boolean;
  movieFolderFormat: string;
  seriesFolderFormat: string;
  episodeFileFormat: string;
  hostBindAddress: string;
  hostPort: number;
  urlBase: string;
  requireAuthentication: boolean;
  uiTheme: string;
  uiDensity: string;
  defaultMovieView: string;
  defaultShowView: string;
  metadataNfoEnabled: boolean;
  metadataArtworkEnabled: boolean;
  metadataCertificationCountry: string;
  metadataLanguage: string;
  metadataProviderMode: "broker" | "hybrid" | "direct" | string;
  metadataBrokerUrl: string;
  metadataBrokerConfigured: boolean;
  metadataTmdbApiKeyConfigured: boolean;
  metadataOmdbApiKeyConfigured: boolean;
  releaseNeverGrabPatterns: string;
  updatedUtc: string;
}

export interface ApiKeyItem {
  id: string;
  name: string;
  prefix: string;
  scopes: string;
  lastUsedUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface CreatedApiKeyResponse {
  item: ApiKeyItem;
  apiKey: string;
}

export const emptyPlatformSettingsSnapshot: PlatformSettingsSnapshot = {
  appInstanceName: "Deluno",
  movieRootPath: null,
  seriesRootPath: null,
  downloadsPath: null,
  incompleteDownloadsPath: null,
  autoStartJobs: true,
  enableNotifications: true,
  renameOnImport: true,
  useHardlinks: false,
  cleanupEmptyFolders: true,
  removeCompletedDownloads: false,
  unmonitorWhenCutoffMet: false,
  movieFolderFormat: "{Movie Title} ({Release Year})",
  seriesFolderFormat: "{Series Title} ({Series Year})",
  episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
  hostBindAddress: "127.0.0.1",
  hostPort: 5099,
  urlBase: "",
  requireAuthentication: true,
  uiTheme: "system",
  uiDensity: "comfortable",
  defaultMovieView: "grid",
  defaultShowView: "grid",
  metadataNfoEnabled: false,
  metadataArtworkEnabled: true,
  metadataCertificationCountry: "US",
  metadataLanguage: "en",
  metadataProviderMode: "direct",
  metadataBrokerUrl: "",
  metadataBrokerConfigured: false,
  metadataTmdbApiKeyConfigured: false,
  metadataOmdbApiKeyConfigured: false,
  releaseNeverGrabPatterns: "cam\ncamrip\ntelesync\ntelecine\nworkprint\nscreener\nsample\ntrailer\nextras",
  updatedUtc: new Date(0).toISOString()
};

export interface LibraryItem {
  id: string;
  name: string;
  mediaType: string;
  purpose: string;
  rootPath: string;
  downloadsPath: string | null;
  qualityProfileId: string | null;
  qualityProfileName: string | null;
  cutoffQuality: string | null;
  upgradeUntilCutoff: boolean;
  upgradeUnknownItems: boolean;
  importWorkflow: "standard" | "refine-before-import" | string;
  processorName: string | null;
  processorOutputPath: string | null;
  processorTimeoutMinutes: number;
  processorFailureMode: "block" | "import-original" | "manual-review" | string;
  autoSearchEnabled: boolean;
  missingSearchEnabled: boolean;
  upgradeSearchEnabled: boolean;
  searchIntervalHours: number;
  retryDelayHours: number;
  maxItemsPerRun: number;
  automationStatus: string;
  searchRequested: boolean;
  lastSearchedUtc: string | null;
  nextSearchUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface LibrarySourceLinkItem {
  id: string;
  libraryId: string;
  indexerId: string;
  indexerName: string;
  priority: number;
  requiredTags: string;
  excludedTags: string;
  createdUtc: string;
  updatedUtc: string;
}

export interface LibraryDownloadClientLinkItem {
  id: string;
  libraryId: string;
  downloadClientId: string;
  downloadClientName: string;
  priority: number;
  createdUtc: string;
  updatedUtc: string;
}

export interface LibraryRoutingSnapshot {
  libraryId: string;
  libraryName: string;
  sources: LibrarySourceLinkItem[];
  downloadClients: LibraryDownloadClientLinkItem[];
}

export interface QualityProfileItem {
  id: string;
  name: string;
  mediaType: string;
  cutoffQuality: string;
  allowedQualities: string;
  customFormatIds: string;
  upgradeUntilCutoff: boolean;
  upgradeUnknownItems: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface TagItem {
  id: string;
  name: string;
  color: string;
  description: string;
  createdUtc: string;
  updatedUtc: string;
}

export interface IntakeSourceItem {
  id: string;
  name: string;
  provider: string;
  feedUrl: string;
  mediaType: string;
  libraryId: string | null;
  libraryName: string | null;
  qualityProfileId: string | null;
  qualityProfileName: string | null;
  searchOnAdd: boolean;
  isEnabled: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface CustomFormatItem {
  id: string;
  name: string;
  mediaType: string;
  score: number;
  conditions: string;
  upgradeAllowed: boolean;
  /** TRaSH Guide identifier when sourced from the built-in library */
  trashId?: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface DestinationRuleItem {
  id: string;
  name: string;
  mediaType: string;
  matchKind: string;
  matchValue: string;
  rootPath: string;
  folderTemplate: string | null;
  priority: number;
  isEnabled: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface ImportPreviewRequest {
  sourcePath: string;
  fileName?: string | null;
  mediaType?: string | null;
  title?: string | null;
  year?: number | null;
  genres?: string[] | null;
  tags?: string[] | null;
  studio?: string | null;
  originalLanguage?: string | null;
}

export interface ImportPreviewResponse {
  sourcePath: string;
  destinationFolder: string;
  destinationPath: string;
  preferredTransferMode: string;
  hardlinkAvailable: boolean;
  matchedRuleId: string | null;
  matchedRuleName: string | null;
  sourceExists: boolean;
  destinationExists: boolean;
  sourceSizeBytes: number;
  destinationSizeBytes: number;
  isSupportedMediaFile: boolean;
  mediaProbe: MediaProbeInfo | null;
  transferExplanation: string;
  warnings: string[];
  explanation: string;
  decisionSteps: string[];
}

export interface MediaProbeInfo {
  status: string;
  tool: string;
  message: string | null;
  durationSeconds: number | null;
  container: string | null;
  bitrate: number | null;
  videoStreams: MediaVideoStreamInfo[];
  audioStreams: MediaAudioStreamInfo[];
  subtitleStreams: MediaSubtitleStreamInfo[];
}

export interface MediaVideoStreamInfo {
  index: number;
  codec: string | null;
  profile: string | null;
  width: number | null;
  height: number | null;
  pixelFormat: string | null;
  frameRate: number | null;
  bitrate: number | null;
  language: string | null;
}

export interface MediaAudioStreamInfo {
  index: number;
  codec: string | null;
  profile: string | null;
  channels: number | null;
  channelLayout: string | null;
  sampleRate: number | null;
  bitrate: number | null;
  language: string | null;
}

export interface MediaSubtitleStreamInfo {
  index: number;
  codec: string | null;
  language: string | null;
}

export interface ImportExecuteRequest {
  preview: ImportPreviewRequest;
  transferMode?: "auto" | "hardlink" | "copy" | "move" | string | null;
  overwrite: boolean;
  allowCopyFallback: boolean;
  forceReplacement?: boolean;
}

export interface ImportExecuteResponse {
  preview: ImportPreviewResponse;
  executed: boolean;
  transferModeUsed: string;
  usedFallback: boolean;
  catalogUpdated: boolean;
  message: string;
}

export interface ImportJobResponse {
  jobId: string;
  preview: ImportPreviewResponse;
  job: JobQueueItem;
}

export interface PolicySetItem {
  id: string;
  name: string;
  mediaType: string;
  qualityProfileId: string | null;
  qualityProfileName: string | null;
  destinationRuleId: string | null;
  destinationRuleName: string | null;
  customFormatIds: string;
  searchIntervalOverrideHours: number | null;
  retryDelayOverrideHours: number | null;
  upgradeUntilCutoff: boolean;
  isEnabled: boolean;
  notes: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface ConnectionItem {
  id: string;
  name: string;
  connectionKind: string;
  role: string;
  endpointUrl: string | null;
  isEnabled: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface IndexerItem {
  id: string;
  name: string;
  protocol: string;
  privacy: string;
  baseUrl: string;
  apiKey?: string | null;
  priority: number;
  categories: string;
  tags: string;
  isEnabled: boolean;
  /** Which media types this indexer covers: "movies" | "tv" | "both" */
  mediaScope?: "movies" | "tv" | "both" | null;
  healthStatus: string;
  lastHealthMessage: string | null;
  lastHealthFailureCategory?: string | null;
  lastHealthLatencyMs?: number | null;
  lastHealthTestUtc?: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface DownloadClientItem {
  id: string;
  name: string;
  /** qbittorrent | sabnzbd | nzbget | transmission | deluge | utorrent */
  protocol: string;
  host?: string | null;
  port?: number | null;
  username?: string | null;
  endpointUrl: string | null;
  /** Category used for movie downloads; maps to a folder/label in the client */
  moviesCategory?: string | null;
  /** Category used for TV show downloads */
  tvCategory?: string | null;
  /** Legacy single category; only used when moviesCategory/tvCategory are absent */
  categoryTemplate: string | null;
  priority: number;
  isEnabled: boolean;
  healthStatus: string;
  lastHealthMessage: string | null;
  lastHealthFailureCategory?: string | null;
  lastHealthLatencyMs?: number | null;
  lastHealthTestUtc?: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface DownloadTelemetrySummary {
  activeCount: number;
  queuedCount: number;
  completedCount: number;
  stalledCount: number;
  processingCount: number;
  importReadyCount: number;
  totalSpeedMbps: number;
}

export interface DownloadQueueItem {
  id: string;
  clientId: string;
  clientName: string;
  protocol: string;
  mediaType: string;
  title: string;
  releaseName: string;
  category: string;
  status: "downloading" | "queued" | "completed" | "stalled" | "processing" | "processed" | "waitingForProcessor" | "importReady" | "importQueued" | "importFailed" | "imported" | "processingFailed" | string;
  progress: number;
  speedMbps: number;
  etaSeconds: number;
  sizeBytes: number;
  downloadedBytes: number;
  peers: number;
  indexerName: string;
  errorMessage: string | null;
  addedUtc: string;
  sourcePath: string | null;
}

export interface DownloadClientHistoryItem {
  id: string;
  clientId: string;
  clientName: string;
  protocol: string;
  mediaType: string;
  title: string;
  releaseName: string;
  category: string;
  outcome: string;
  indexerName: string;
  sizeBytes: number;
  completedUtc: string;
  errorMessage: string | null;
  sourcePath: string | null;
}

export interface DownloadClientTelemetrySnapshot {
  clientId: string;
  clientName: string;
  protocol: string;
  endpointUrl: string | null;
  healthStatus: string;
  lastHealthMessage: string | null;
  summary: DownloadTelemetrySummary;
  queue: DownloadQueueItem[];
  history: DownloadClientHistoryItem[];
  capturedUtc: string;
}

export interface DownloadTelemetryOverview {
  summary: DownloadTelemetrySummary;
  clients: DownloadClientTelemetrySnapshot[];
  capturedUtc: string;
}

export interface ConnectionTestResponse {
  healthStatus: string;
  message: string;
}

export interface MetadataSearchResult {
  provider: string;
  providerId: string;
  mediaType: string;
  title: string;
  originalTitle: string | null;
  year: number | null;
  overview: string | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  rating: number | null;
  ratings?: MetadataRatingItem[] | null;
  genres: string[];
  imdbId: string | null;
  externalUrl: string | null;
}

export interface MetadataRatingItem {
  source: string;
  label: string;
  score: number | null;
  maxScore: number | null;
  voteCount: number | null;
  url: string | null;
  kind: string | null;
}

export interface MetadataProviderStatus {
  provider: string;
  isConfigured: boolean;
  mode: "live" | "unconfigured" | string;
  message: string;
  sources: MetadataSourceStatus[];
}

export interface MetadataSourceStatus {
  source: string;
  label: string;
  role: string;
  isConfigured: boolean;
  mode: string;
  message: string;
}

export interface MetadataTestResponse {
  provider: string;
  isConfigured: boolean;
  mode: string;
  message: string;
  resultCount: number;
  sampleResults: MetadataSearchResult[];
}

export interface MetadataRefreshJobsResponse {
  enqueuedCount: number;
  jobs: JobQueueItem[];
}

export interface JobQueueItem {
  id: string;
  jobType: string;
  source: string;
  status: string;
  payloadJson: string | null;
  attempts: number;
  createdUtc: string;
  scheduledUtc: string;
  startedUtc: string | null;
  completedUtc: string | null;
  leasedUntilUtc: string | null;
  workerId: string | null;
  lastError: string | null;
  relatedEntityType: string | null;
  relatedEntityId: string | null;
}

export interface LibraryAutomationStateItem {
  libraryId: string;
  libraryName: string;
  mediaType: string;
  status: string;
  searchRequested: boolean;
  lastPlannedUtc: string | null;
  lastStartedUtc: string | null;
  lastCompletedUtc: string | null;
  nextSearchUtc: string | null;
  lastJobId: string | null;
  lastError: string | null;
  updatedUtc: string;
}

export interface SearchCycleRunItem {
  id: string;
  libraryId: string;
  libraryName: string;
  mediaType: string;
  triggerKind: string;
  status: string;
  plannedCount: number;
  queuedCount: number;
  skippedCount: number;
  notesJson: string | null;
  startedUtc: string;
  completedUtc: string | null;
}

export interface SearchRetryWindowItem {
  entityType: string;
  entityId: string;
  libraryId: string;
  mediaType: string;
  actionKind: string;
  nextEligibleUtc: string;
  lastAttemptUtc: string;
  attemptCount: number;
  lastResult: string | null;
  updatedUtc: string;
}

export interface ActivityEventItem {
  id: string;
  category: string;
  message: string;
  severity?: "info" | "success" | "warning" | "error";
  detail?: string;
  detailsJson: string | null;
  relatedJobId: string | null;
  relatedEntityType: string | null;
  relatedEntityId: string | null;
  createdUtc: string;
}

export interface DecisionAlternativeExplanation {
  name: string;
  status: string;
  reason: string;
  score: number | null;
}

export interface DecisionExplanationItem {
  id: string;
  occurredUtc: string;
  scope: string;
  status: string;
  reason: string;
  inputs: Record<string, string | null>;
  outcome: string;
  alternatives: DecisionAlternativeExplanation[];
  relatedJobId: string | null;
  relatedEntityType: string | null;
  relatedEntityId: string | null;
}

export interface BackupSettingsSnapshot {
  enabled: boolean;
  frequency: string;
  timeOfDay: string;
  retentionCount: number;
  backupFolder: string;
  lastRunUtc: string | null;
  nextRunUtc: string | null;
}

export interface BackupItem {
  id: string;
  fileName: string;
  fullPath: string;
  sizeBytes: number;
  createdUtc: string;
  reason: string;
}

export interface RestorePreviewResponse {
  valid: boolean;
  message: string;
  manifest: {
    app: string;
    version: string;
    createdUtc: string;
    reason: string;
    files: string[];
  } | null;
  warnings: string[];
}

export interface UpdateStatusResponse {
  currentVersion: string;
  channel: string;
  updateAvailable: boolean;
  latestVersion: string | null;
  message: string;
  notes: string[];
}

export interface DownloadDispatchItem {
  id: string;
  libraryId: string;
  mediaType: string;
  entityType: string;
  entityId: string;
  releaseName: string;
  indexerName: string;
  downloadClientId: string;
  downloadClientName: string;
  status: string;
  notesJson: string | null;
  createdUtc: string;
}

export interface DirectoryBrowseEntry {
  name: string;
  path: string;
  kind: "root" | "directory" | "preset";
  description: string | null;
}

export interface DirectoryBrowseResponse {
  currentPath: string | null;
  parentPath: string | null;
  entries: DirectoryBrowseEntry[];
}

export interface PathDiagnosticResponse {
  path: string;
  normalizedPath: string;
  exists: boolean;
  isDirectory: boolean;
  parentExists: boolean;
  readable: boolean;
  writable: boolean;
  serverCanBrowse: boolean;
  isUncPath: boolean;
  isLikelyDockerPath: boolean;
  message: string;
  warnings: string[];
}

export interface LibraryViewItem {
  id: string;
  userId: string;
  variant: "movies" | "shows";
  name: string;
  quickFilter: string;
  sortField: string;
  sortDirection: "asc" | "desc";
  viewMode: "grid" | "list";
  cardSize: "sm" | "md" | "lg";
  displayOptionsJson: string;
  rulesJson: string;
  createdUtc: string;
  updatedUtc: string;
}

export interface MigrationImportRequest {
  sourceKind: "radarr" | "sonarr" | "prowlarr" | "recyclarr" | "custom" | string;
  sourceName: string;
  payloadJson: string;
}

export interface MigrationReportSummary {
  createCount: number;
  skipCount: number;
  conflictCount: number;
  unsupportedCount: number;
  warningCount: number;
  titleCount: number;
  monitoredCount: number;
  wantedCount: number;
}

export interface MigrationReportOperation {
  id: string;
  category: string;
  targetType: string;
  name: string;
  action: "create" | "skip" | "conflict" | "unsupported" | "report" | string;
  canApply: boolean;
  reason: string;
  data: Record<string, string | null>;
  warnings: string[];
}

export interface MigrationReport {
  sourceKind: string;
  sourceName: string;
  valid: boolean;
  summary: MigrationReportSummary;
  operations: MigrationReportOperation[];
  warnings: string[];
  errors: string[];
}

export interface MigrationAppliedItem {
  operationId: string;
  targetType: string;
  name: string;
  createdId: string;
  result: string;
}

export interface MigrationApplyResponse {
  report: MigrationReport;
  applied: MigrationAppliedItem[];
}

export interface CreateLibraryViewRequest {
  variant: "movies" | "shows";
  name: string;
  quickFilter: string;
  sortField: string;
  sortDirection: "asc" | "desc";
  viewMode: "grid" | "list";
  cardSize: "sm" | "md" | "lg";
  displayOptionsJson: string;
  rulesJson: string;
}

export interface UpdateLibraryViewRequest {
  name: string;
  quickFilter: string;
  sortField: string;
  sortDirection: "asc" | "desc";
  viewMode: "grid" | "list";
  cardSize: "sm" | "md" | "lg";
  displayOptionsJson: string;
  rulesJson: string;
}

export class ApiRequestError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly path: string,
    public readonly responseBody: string
  ) {
    super(message);
    this.name = "ApiRequestError";
  }
}

export async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await authedFetch(path, init);
  if (!response.ok) {
    const responseBody = await response.text().catch(() => "");
    let message = `Request failed for ${path} with status ${response.status}.`;

    if (responseBody) {
      try {
        const parsed = JSON.parse(responseBody) as { message?: unknown; title?: unknown };
        const serverMessage =
          typeof parsed.message === "string"
            ? parsed.message
            : typeof parsed.title === "string"
              ? parsed.title
              : null;

        if (serverMessage) {
          message = serverMessage;
        }
      } catch {
        message = responseBody;
      }
    }

    throw new ApiRequestError(message, response.status, path, responseBody);
  }

  return (await response.json()) as T;
}

export async function readValidationProblem(
  response: Response
): Promise<ValidationProblem | null> {
  try {
    return (await response.json()) as ValidationProblem;
  } catch {
    return null;
  }
}

import type { JobQueueItem } from "./operations";

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
  cleanupMode?: "keep-source" | "remove-source-after-import" | string;
  removeEmptySourceFolders?: boolean;
  autoSearchEnabled: boolean;
  missingSearchEnabled: boolean;
  upgradeSearchEnabled: boolean;
  searchIntervalHours: number;
  retryDelayHours: number;
  maxItemsPerRun: number;
  searchWindowStartHour: number | null;
  searchWindowEndHour: number | null;
  automationStatus: string;
  searchRequested: boolean;
  lastSearchedUtc: string | null;
  nextSearchUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
  defaultPolicySetId?: string | null;
  defaultPolicySetName?: string | null;
  /** Ordered ISO 639-1 codes, most wanted first. Empty means no subtitles are wanted here. */
  subtitleLanguages?: string[] | null;
  /** `all` — every language listed. `first` — the first one that can be found. */
  subtitleLanguageMode?: "all" | "first" | string;
  /**
   * What a subtitle with no language in its name is taken to be.
   *
   * Empty means "do not guess", which is the default and what Deluno has always
   * done — reading a bare `Movie.srt` as the first wanted language would be
   * right most of the time and silently wrong the rest (DESIGN-002, #321).
   */
  subtitleUnknownLanguage?: string;
  /** Whether a subtitle track inside the video counts as held. On by default. */
  subtitleEmbeddedCounts?: boolean;
  /** Named post-download subtitle cleanups. */
  subtitleContentPolicy?: SubtitleContentModificationPolicy | null;
  /** Automatic timing repair policy for fetched subtitles. */
  subtitleTimingPolicy?: SubtitleTimingPolicy | null;
}

export interface SubtitleContentModificationPolicy {
  stripHearingImpairedAnnotations: boolean;
  removeStyleTags: boolean;
  removeEmoji: boolean;
  normalizeWhitespace: boolean;
  fixAllUppercase: boolean;
}

export interface SubtitleTimingPolicy {
  enabled: boolean;
  syncOnlyBelow: "same-source" | "made-for-this-file" | string;
  maxOffsetSeconds: number;
  requiredPeakSigma: number;
  excludedProviders: string[] | null;
}

/** One language Deluno can name, from GET /api/subtitle-languages. */
export interface SubtitleLanguageOption {
  code: string;
  name: string;
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
  category?: string | null;
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
  allowLowerQualityReplacements: boolean;
  presetId: string | null;
  presetVersion: number | null;
  presetDrifted: boolean;
  releasePreferencePlan: ReleasePreferencePlanReference | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface ReleasePreferencePlanReference {
  planId: string;
  version: string;
  planHash: string;
}

export interface TagItem {
  id: string;
  name: string;
  color: string;
  description: string;
  createdUtc: string;
  updatedUtc: string;
}

export interface TagUsageItem {
  id: string;
  name: string;
  movieCount: number;
  seriesCount: number;
  totalCount: number;
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
  requiredGenres: string;
  minimumRating: number | null;
  minimumYear: number | null;
  maximumAgeDays: number | null;
  allowedCertifications: string;
  audience: "any" | "kids" | "adult" | string;
  syncIntervalHours: number;
  lastSyncUtc: string | null;
  lastSyncStatus: "never" | "success" | "partial" | "error" | string;
  lastSyncSummary: string | null;
  searchOnAdd: boolean;
  isEnabled: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface IntakeListExclusionItem {
  id: string;
  sourceId: string;
  entryKey: string;
  title: string;
  year: number | null;
  imdbId: string | null;
  reason: string;
  expiresUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface MediaExclusionItem {
  id: string;
  mediaType: string;
  sourceKind: string;
  sourceId: string;
  sourceName: string;
  provider: string;
  entryKey: string;
  title: string;
  year: number | null;
  imdbId: string | null;
  reason: string;
  expiresUtc: string | null;
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
  imdbId?: string | null;
  tvDbId?: string | null;
  network?: string | null;
  qualityProfile?: string | null;
  seriesId?: string | null;
  seriesType?: string | null;
  numberingScheme?: string | null;
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
  pack?: ImportPackPreview | null;
}

export interface ImportPackPreview {
  canExecute: boolean;
  alreadyCommitted: boolean;
  sourceFileCount: number;
  episodeCount: number;
  files: ImportPackFilePreview[];
  blockReasons: string[];
}

export interface ImportPackFilePreview {
  sourcePath: string;
  destinationPath: string;
  sourceSizeBytes: number;
  episodeKeys: string[];
  warnings: string[];
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
  forced?: boolean;
  hearingImpaired?: boolean;
  title?: string | null;
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
  dispatchId?: string | null;
  expectedExistingPath?: string | null;
}

export interface ImportExecuteResponse {
  preview: ImportPreviewResponse;
  executed: boolean;
  transferModeUsed: string;
  usedFallback: boolean;
  catalogUpdated: boolean;
  message: string;
  packFiles?: ImportPackFileResult[] | null;
}

export interface ImportPackFileResult {
  sourcePath: string;
  destinationPath: string;
  episodeKeys: string[];
  transferModeUsed: string;
}

export interface ImportJobResponse {
  jobId: string;
  preview: ImportPreviewResponse;
  job: JobQueueItem;
}

export interface MediaPlanAutomationIntent {
  scenarioId: string | null;
  scenarioVersion: number | null;
  sizeTierId: string | null;
  sizeTierName: string | null;
  sizeDescription: string | null;
  subtitleIntent: string | null;
  routingIntent: string | null;
  sharingIntent: string | null;
  cleanupIntent: string | null;
  notificationIntent: string | null;
  namingIntent: string | null;
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
  automationIntent: MediaPlanAutomationIntent | null;
  releasePreferencePlan: ReleasePreferencePlanReference | null;
}

export interface MediaPlanSnapshot {
  name: string;
  mediaType: string;
  qualityProfileId: string | null;
  destinationRuleId: string | null;
  customFormatIds: string;
  searchIntervalOverrideHours: number | null;
  retryDelayOverrideHours: number | null;
  upgradeUntilCutoff: boolean;
  isEnabled: boolean;
  notes: string | null;
  automationIntent: MediaPlanAutomationIntent | null;
  releasePreferencePlan: ReleasePreferencePlanReference | null;
}

export interface MediaPlanVersionItem {
  planId: string;
  version: number;
  planHash: string;
  changeKind: string;
  snapshot: MediaPlanSnapshot;
  createdUtc: string;
}

export interface MediaPlanDiffItem {
  field: string;
  currentValue: string | null;
  proposedValue: string | null;
}

export interface MediaPlanPreview {
  planId: string;
  currentVersion: number | null;
  current: MediaPlanSnapshot;
  proposed: MediaPlanSnapshot;
  changes: MediaPlanDiffItem[];
  hasChanges: boolean;
  basePlanHash: string | null;
}

export interface MediaPlanLayerOverride {
  qualityProfileId?: string | null;
  destinationRuleId?: string | null;
  customFormatIds?: string | null;
  searchIntervalOverrideHours?: number | null;
  retryDelayOverrideHours?: number | null;
  upgradeUntilCutoff?: boolean | null;
  isEnabled?: boolean | null;
  notes?: string | null;
  automationIntent?: MediaPlanAutomationIntent | null;
  releasePreferencePlan?: ReleasePreferencePlanReference | null;
}

export interface MediaPlanFieldResolution {
  field: string;
  value: string | null;
  sourceKind: string;
  sourceId: string | null;
  isSafetyLocked: boolean;
}

export interface MediaPlanEffectiveResolution {
  basePlan: MediaPlanSnapshot;
  effectivePlan: MediaPlanSnapshot;
  fields: MediaPlanFieldResolution[];
  warnings: string[];
}

export type PlaybackCapabilityState = "present" | "absent" | "unknown" | "conflicting" | string;

export interface PlaybackCapability {
  traitId: string;
  state: PlaybackCapabilityState;
  source: string;
  confidence: number | null;
  detail: string | null;
  lastConfirmedUtc: string | null;
}

export interface PlaybackDeviceProfile {
  id: string;
  name: string;
  capabilities: PlaybackCapability[];
  isEnabled: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface PlaybackDeviceGroup {
  id: string;
  name: string;
  mode: "every-device" | "primary-device" | "fallback" | string;
  deviceProfileIds: string[];
  primaryDeviceProfileId: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface PlaybackGoalItem {
  id: string;
  name: string;
  mediaType: "movies" | "tv" | string;
  deviceGroupId: string;
  mustPlay: boolean;
  requiredTraitIds: string[];
  requiredAnyTraitGroups: string[][];
  forbiddenTraitIds?: string[];
  preferredTraitIds: string[];
  stopWhenTraitId: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface PlaybackGoalCompilation {
  goal: PlaybackGoalItem;
  group: PlaybackDeviceGroup | null;
  selectedDevices: PlaybackDeviceProfile[];
  plan: import("./release-preferences").ReleasePreferencePlan;
  planHash: string;
  unknownCapabilities: string[];
  warnings: string[];
  requiresReview: boolean;
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

export interface IntegrationFailure {
  serviceType: string;
  serviceId: string;
  serviceName: string;
  operation: string;
  kind: "Authentication" | "RateLimit" | "Timeout" | "Protocol" | "Unavailable" | "MalformedResponse" | "RejectedAction" | "Configuration" | "CircuitOpen" | "Unknown" | string;
  retryState: "NotRetryable" | "Retrying" | "RetryScheduled" | "CircuitOpen" | "ManualAction" | string;
  message: string;
  code: string | null;
  httpStatus: number | null;
  upstreamDetail: string | null;
  externalId: string | null;
  retryAfterUtc: string | null;
  attempts: number;
  isTransient: boolean;
  legacyCategory: string;
  summary: string;
  nextAction: string;
}

export interface IndexerItem {
  id: string;
  name: string;
  protocol: string;
  /**
   * What the app you migrated from called this source: "private",
   * "semi-private", "public", or "unknown" for anything Deluno added itself.
   *
   * Provenance, not configuration. Deluno branches on none of it — the sharing
   * rule below is what changes behaviour — so nothing sets it and nothing
   * displays it. It earns its place by pre-answering the sharing question for
   * an imported private tracker (#288).
   */
  privacy: string;
  baseUrl: string;
  apiKey?: string | null;
  priority: number;
  requestIntervalSeconds?: number | null;
  categories: string;
  tags: string;
  isEnabled: boolean;
  /** Which media types this indexer covers: "movies" | "tv" | "both" */
  mediaScope?: "movies" | "tv" | "both" | null;
  healthStatus: string;
  lastHealthMessage: string | null;
  lastHealthFailureCategory?: string | null;
  lastHealthFailure?: IntegrationFailure | null;
  lastHealthLatencyMs?: number | null;
  lastHealthTestUtc?: string | null;
  consecutiveFailures: number;
  rateLimitedUntilUtc: string | null;
  disabledReason: string | null;
  createdUtc: string;
  updatedUtc: string;
  /**
   * This source's own sharing rule (#288). Null everywhere means "inherit the
   * global setting", which is what almost every source does — the requirement
   * comes from the site, so only a site that is stricter has to say anything.
   */
  sharingMode?: string | null;
  sharingForHours?: number | null;
  sharingUntilRatio?: number | null;
  sharingStuckAction?: string | null;
  sharingStuckAfterDays?: number | null;
  minimumAgeMinutes?: number | null;
  retentionDays?: number | null;
  maximumSizeMb?: number | null;
  preferIndexerFlags?: string | null;
  availabilityDelayDays?: number | null;
  rssEnabled?: boolean;
  automaticSearchEnabled?: boolean;
  interactiveSearchEnabled?: boolean;
}

export interface ReleaseTermScore {
  term: string;
  score: number;
}

export interface ReleaseProfileItem {
  id: string;
  name: string;
  tagName: string;
  preferredProtocol: "any" | "usenet" | "torrent" | string;
  usenetDelayMinutes: number;
  torrentDelayMinutes: number;
  mustContain: string;
  mustNotContain: string;
  preferredTerms: ReleaseTermScore[];
  createdUtc: string;
  updatedUtc: string;
}

export interface IndexerScoreboardRow {
  id: string;
  name: string;
  isEnabled: boolean;
  healthStatus: string;
  totalQueries: number;
  searchQueries: number;
  rssQueries: number;
  authQueries: number;
  failedQueries: number;
  failureRate: number;
  averageResponseMilliseconds: number | null;
  candidatesReturned: number;
  totalGrabs: number;
  successfulGrabs: number;
  queryToGrabConversion: number | null;
  recommendation: string;
}

export interface IndexerScoreboardSnapshot {
  windowDays: number;
  fromUtc: string;
  toUtc: string;
  activeIndexers: number;
  totalIndexers: number;
  totalQueries: number;
  totalGrabs: number;
  successfulGrabs: number;
  conversionRate: number | null;
  insight: string;
  indexers: IndexerScoreboardRow[];
}

export interface OutboundThrottleHostState {
  host: string;
  waiting: number;
  grantedCount: number;
  refusedCount: number;
  totalWaitedSeconds: number;
  nextPermitInSeconds: number;
}

export interface OutboundThrottleSnapshot {
  hosts: OutboundThrottleHostState[];
}

export interface NotificationWebhookItem {
  id: string;
  name: string;
  url: string;
  eventFilters: string;
  isEnabled: boolean;
  lastFiredUtc: string | null;
  lastError: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface NotificationWebhookDeliveryItem {
  id: string;
  webhookId: string;
  eventCategory: string;
  title: string;
  status: "pending" | "retrying" | "delivered" | "dead-letter" | string;
  attemptCount: number;
  maxAttempts: number;
  nextAttemptUtc: string | null;
  lastAttemptUtc: string | null;
  lastStatusCode: number | null;
  lastError: string | null;
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
  lastHealthFailure?: IntegrationFailure | null;
  lastHealthLatencyMs?: number | null;
  lastHealthTestUtc?: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface ExistingLibraryCandidate {
  sourcePath: string;
  relativePath: string;
  title: string;
  year: number | null;
  detectedQuality: string | null;
  fileSizeBytes: number | null;
  isDirectory: boolean;
  canImport: boolean;
  issueKind: string | null;
  issueDetail: string | null;
}

export interface ExistingLibraryPreviewPage {
  libraryId: string;
  libraryName: string;
  mediaType: string;
  rootPath: string;
  items: ExistingLibraryCandidate[];
  nextCursor: string | null;
  hasMore: boolean;
}

export interface ExistingLibraryImportResult {
  requestedCount: number;
  importedCount: number;
  skippedCount: number;
  issues: Array<{
    sourcePath: string;
    kind: string;
    detail: string;
  }>;
}

export interface DownloadClientCategoryCheckResult {
  clientId: string;
  clientName: string;
  category: string;
  status: "ready" | "missing" | "unsupported" | "unreachable" | "configuration" | string;
  message: string;
  supported: boolean;
  found: boolean;
}

export interface DownloadTelemetrySummary {
  activeCount: number;
  queuedCount: number;
  completedCount: number;
  stalledCount: number;
  /** Post-processing and import work together; see waitingForProcessorCount. */
  processingCount: number;
  importReadyCount: number;
  totalSpeedMbps: number;
  /** Combined upload across every client, in MB/s. Sharing is a first-class concern (#288). */
  totalUploadSpeedMbps: number;
  /** The processor share of processingCount — held back awaiting a cleaned output. */
  waitingForProcessorCount: number;
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
  libraryId: string | null;
  healthFindings: DownloadHealthFinding[] | null;
  failure?: IntegrationFailure | null;
}

/** A client-specific translation from the path it reports to the path Deluno can access. */
export interface DownloadClientPathMappingItem {
  id: string;
  downloadClientId: string;
  remotePath: string;
  localPath: string;
  isEnabled: boolean;
  priority: number;
  createdUtc: string;
  updatedUtc: string;
}

export interface DownloadHealthFinding {
  severity: "critical" | "warning" | string;
  kind: string;
  summary: string;
  evidence: string;
  recommendedAction: string;
  canSafelyRetry: boolean;
  canSafelyRemove: boolean;
  strikeCount: number;
  candidateBlocked: boolean;
  ignoredUntilUtc: string | null;
}

export interface DownloadHealthRecord {
  clientId: string;
  queueItemId: string;
  releaseName: string;
  kind: string;
  severity: string;
  evidence: string;
  firstObservedUtc: string;
  lastObservedUtc: string;
  strikeCount: number;
  ignoredUntilUtc: string | null;
}

export interface ProcessorHandoffItem {
  id: string;
  libraryId: string;
  mediaType: string;
  clientId: string;
  queueItemId: string;
  releaseName: string;
  sourcePath: string;
  processorName: string | null;
  status: string;
  outputPath: string | null;
  importJobId: string | null;
  failureMessage: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface ProcessorConnectionItem {
  id: string;
  name: string;
  provider: "fileflows-webhook" | "generic-webhook" | string;
  submissionUrl: string;
  authHeaderName: string;
  secretConfigured: boolean;
  isEnabled: boolean;
  healthStatus: "unknown" | "healthy" | "degraded" | "unreachable" | string;
  lastHealthMessage: string | null;
  lastHealthTestUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

export interface ProcessorConnectionTestResult {
  connectionId: string;
  isReachable: boolean;
  status: string;
  message: string;
  statusCode: number | null;
  latencyMs: number | null;
}

export interface IntakeListPreviewItem {
  title: string;
  year: number | null;
  mediaType: string;
  imdbId: string | null;
  action: string;
  reason: string;
  matchConfidence: string;
  exclusionId?: string | null;
}

export interface IntakeListPreviewResult {
  sourceId: string;
  sourceName: string;
  provider: string;
  mediaType: string;
  targetLibraryName: string | null;
  fetchedCount: number;
  shownCount: number;
  isTruncated: boolean;
  items: IntakeListPreviewItem[];
  warnings: string[];
}

export interface IntakeListApprovalResult {
  selectedCount: number;
  matchedCount: number;
  addedCount: number;
  duplicateCount: number;
  skippedCount: number;
  errorCount: number;
  searchRequested: boolean;
  summary: string;
}

export interface IntakeTitleOriginItem {
  id: string;
  sourceId: string;
  sourceName: string;
  provider: string;
  mediaType: string;
  entityId: string;
  entryKey: string;
  title: string;
  year: number | null;
  imdbId: string | null;
  firstSeenUtc: string;
  lastSeenUtc: string;
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
  historySource?: "native" | "queue-derived" | "dispatch-derived" | "inferred" | string;
  externalId?: string | null;
  failure?: IntegrationFailure | null;
}

export interface DownloadClientTelemetryCapabilities {
  supportsQueue: boolean;
  supportsHistory: boolean;
  supportsPauseResume: boolean;
  supportsRemove: boolean;
  supportsRecheck: boolean;
  supportsImportPath: boolean;
  authMode: string;
}

export interface DownloadClientTelemetrySnapshot {
  clientId: string;
  clientName: string;
  protocol: string;
  endpointUrl: string | null;
  healthStatus: string;
  lastHealthMessage: string | null;
  capabilities: DownloadClientTelemetryCapabilities;
  summary: DownloadTelemetrySummary;
  queue: DownloadQueueItem[];
  history: DownloadClientHistoryItem[];
  capturedUtc: string;
  historyTruncated: boolean;
  lastFailure?: IntegrationFailure | null;
}

/** One stored reading of combined throughput, both directions. */
export interface DownloadThroughputSample {
  capturedUtc: string;
  speedMbps: number;
  activeCount: number;
  /** Zero on readings taken before upload was measured, which is the truth about them. */
  uploadMbps: number;
}

/** A stored window of throughput readings, oldest first. */
export interface DownloadThroughputWindow {
  hours: number;
  samples: DownloadThroughputSample[];
}

export interface DownloadTelemetryOverview {
  summary: DownloadTelemetrySummary;
  clients: DownloadClientTelemetrySnapshot[];
  capturedUtc: string;
}

/** One finished download the client is still sharing, and why (#288). */
export interface DownloadSharingHold {
  clientId: string;
  clientName: string;
  queueItemId: string;
  title: string;
  /**
   * The evaluator's own words, recorded when it decided — never rewritten here.
   * States only what its heading does not already say: "2 days left", not
   * "Still sharing — 2 days left."
   */
  detail: string;
  sizeBytes: number;
  /** The rule can no longer be met and Deluno was told to ask rather than act. */
  needsYou: boolean;
  /** This copy and the library's are one set of file data, so sharing costs nothing. */
  sharesLibraryCopy: boolean;
}

/** What the download clients are still holding after import (#288). */
export interface DownloadSharingSnapshot {
  holds: DownloadSharingHold[];
  /** Disk held that the library copy does not already account for. */
  extraBytes: number;
  /** Present only when the two copies are genuinely two. */
  driveNote: string | null;
  /** Null when no sharing pass has run recently enough to be worth showing. */
  observedUtc: string | null;
}

export interface ConnectionTestResponse {
  healthStatus: string;
  message: string;
}

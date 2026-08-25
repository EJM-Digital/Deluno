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
  requestIntervalSeconds?: number | null;
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
  consecutiveFailures: number;
  rateLimitedUntilUtc: string | null;
  disabledReason: string | null;
  createdUtc: string;
  updatedUtc: string;
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
  libraryId: string | null;
  healthFindings: DownloadHealthFinding[] | null;
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

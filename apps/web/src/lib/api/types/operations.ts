import type { IntegrationFailure } from "./resources";

export interface JobQueueItem {
  id: string;
  jobType: string;
  /** The job in plain words (#1086). Show this, never jobType. */
  label: string;
  source: string;
  /** What started the job in plain words (#1086). Show this, never source. */
  sourceLabel: string;
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
  idempotencyKey: string | null;
  dedupeKey: string | null;
  // The retry facts the API has always sent and this type did not name, so the
  // screens could not tell a job that will try again from one that stopped (#562).
  maxAttempts: number;
  lastAttemptUtc: string | null;
  /** The episode a search is for, such as "Severance S02E10" (#1180). Null for a job that is not about an episode. */
  target?: string | null;
  /** The show an episode search belongs to, so a show-wide search reads as one entry. */
  showId?: string | null;
  showTitle?: string | null;
  nextAttemptUtc: string | null;
}

export interface SystemTaskItem {
  key: string;
  name: string;
  description: string;
  intervalSeconds: number;
  isConfigurable: boolean;
  lastStartedUtc: string | null;
  lastCompletedUtc: string | null;
  lastResult: string;
  lastDurationMs: number | null;
  nextRunUtc: string | null;
}

export interface RecycleBinSettings {
  retentionDays: number;
  maxSizeMb: number;
}

export interface RecycleBinItem {
  id: string;
  libraryId: string;
  libraryName: string;
  mediaType: string;
  originalPath: string;
  recyclePath: string;
  isDirectory: boolean;
  sizeBytes: number;
  createdUtc: string;
  expiresUtc: string;
  /** Set when the files left because a title was removed: Restore brings the title back too (#1161). */
  titleSnapshotId?: string | null;
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
  nextMissingSearchUtc?: string | null;
  nextUpgradeSearchUtc?: string | null;
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
  searchKind?: string;
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
  /** The kind of event in plain words (#1086); show this, never `category`. */
  label?: string;
  message: string;
  severity?: "info" | "success" | "warning" | "error";
  detail?: string;
  detailsJson: string | null;
  relatedJobId: string | null;
  relatedEntityType: string | null;
  relatedEntityId: string | null;
  createdUtc: string;
  /** The title (film, show, season or episode) the event concerns (#1100). */
  title?: string | null;
  /** The file path or paths (one per line) the event concerns (#1100). */
  filePath?: string | null;
  /** The indexer, download client, processor or connection it came from (#1100). */
  sourceName?: string | null;
  /** For a turned-down release, the plain group of its reason (#1100). */
  reasonGroup?: string | null;
  /** The plain kind of event (#1100). */
  type?: string;
  /** indexer, client, processor or connection when an outside thing was involved (#1100). */
  source?: string | null;
  /** good or problem (#1100). */
  outcome?: "good" | "problem";
}

/** GET /api/activity/filters: the words each Activity filter offers (#1100). */
export interface ActivityFilterChoices {
  types: { value: string; label: string }[];
  sources: { value: string; label: string }[];
  outcomes: { value: string; label: string }[];
  reasons: { value: string; label: string }[];
}

/**
 * The counts behind GET /api/stats (#827) -- the same facts the Activity feed
 * already writes in plain words, aggregated over a time window for an
 * external dashboard widget (Homepage, Homarr) or Deluno's own Dashboard.
 */
export interface ActivityStatsSnapshot {
  sinceUtc: string;
  generatedUtc: string;
  windowHours: number;
  searchesRun: number;
  downloadsStarted: number;
  importsCompleted: number;
  cleanupRemovals: number;
  retentionRemovals: number;
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
  /** What the decision was about, in plain words (#1086); show this, never `scope`. */
  scopeLabel: string;
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
  /** Plain words when the saved settings could not be read and the standard ones are in use; saving again repairs them. */
  problem?: string | null;
  /** True when a backup password is saved: Deluno's keys then go into each backup, sealed with it. The password itself is never sent back. */
  passwordSet?: boolean;
  /** Sent when saving: a new backup password. Leave out to keep the current one. */
  backupPassword?: string;
  /** Sent when saving: true removes the saved password. */
  clearBackupPassword?: boolean;
}

export interface BackupItem {
  id: string;
  fileName: string;
  fullPath: string;
  sizeBytes: number;
  createdUtc: string;
  reason: string;
  keysIncluded?: boolean;
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
  newerThanThisVersion?: boolean;
  /** True when the backup holds Deluno's keys, locked with a backup password that restoring must be given. */
  needsPassword?: boolean;
}

export interface UpdateStatusResponse {
  currentVersion: string;
  installKind: string;
  behaviorMode: string;
  isInstalled: boolean;
  canCheck: boolean;
  canDownload: boolean;
  canApply: boolean;
  channel: string;
  updateAvailable: boolean;
  latestVersion: string | null;
  state: string;
  progressPercent: number | null;
  restartRequired: boolean;
  lastCheckedUtc: string | null;
  lastDownloadedUtc: string | null;
  message: string;
  lastError: string | null;
  notes: string[];
  currentImageRef?: string | null;
  currentImageDigest?: string | null;
  changelog?: string | null;
  appliedUpdates?: AppliedUpdateEntry[];
}

/** One entry in the "past 5 updates applied" history (#870). */
export interface AppliedUpdateEntry {
  version: string;
  appliedAtUtc: string;
  /** This version's plain-language release notes, or null when the build has no section for it. */
  notes?: string | null;
}

export interface QualityTierDefinition {
  name: string;
  rank: number;
  movieMinGb: number;
  moviePreferredGb: number;
  movieMaxGb: number;
  episodeMinMb: number;
  episodePreferredMb: number;
  episodeMaxMb: number;
  scoreCeiling: number;
}

export interface QualityUpgradeStopPolicy {
  stopWhenCutoffMet: boolean;
  requireCustomFormatGainForSameQuality: boolean;
}

export interface QualityModelSnapshot {
  version: string;
  tiers: QualityTierDefinition[];
  upgradeStop: QualityUpgradeStopPolicy;
  updatedUtc: string;
}

export interface UpdatePreferencesResponse {
  mode: string;
  channel: string;
  autoCheck: boolean;
}

export interface UpdateActionResponse {
  accepted: boolean;
  message: string;
  status: UpdateStatusResponse;
}

export interface MonitoringAlertItem {
  code: string;
  severity: string;
  summary: string;
  details: string;
  detectedUtc: string;
}

export interface MonitoringApiLatencySnapshot {
  windowStartUtc: string;
  windowEndUtc: string;
  requestCount: number;
  errorCount: number;
  errorRatePercent: number;
  averageMs: number;
  p95Ms: number;
}

export interface MonitoringReadinessSummary {
  status: string;
  ready: boolean;
  totalChecks: number;
  failedChecks: number;
}

export interface MonitoringStorageSummary {
  dataRoot: string;
  totalBytes: number | null;
  freeBytes: number | null;
  freePercent: number | null;
  lowStorage: boolean;
}

export interface MonitoringServiceSummary {
  indexersHealthy: number;
  indexersTotal: number;
  downloadClientsHealthy: number;
  downloadClientsTotal: number;
  activeJobs: number;
  queuedJobs: number;
  failedJobs: number;
  openDispatchAlerts: number;
}

export interface MonitoringPerformanceSummary {
  searchCyclesSampled: number;
  averageSearchCycleSeconds: number | null;
  averageGrabToDetectionSeconds: number | null;
  averageDetectionToImportSeconds: number | null;
  apiLatency: MonitoringApiLatencySnapshot;
}

/**
 * One reading of how hard the machine is working (#272).
 *
 * Nulls are meaningful: a whole-volume figure comes from the volume itself and
 * can be refused. An absent cell says "not measured"; a zero would say "idle",
 * which is a different and possibly false claim.
 */
export interface MachineTelemetrySample {
  capturedUtc: string;
  /** Deluno's own share of the machine, already divided by processor count. */
  cpuPercent: number;
  memoryBytes: number;
  totalMemoryBytes: number | null;
  memoryPercent: number | null;
  /** What Deluno itself is doing to the disk — answers "is this Deluno?". */
  processReadBytesPerSecond: number;
  processWriteBytesPerSecond: number;
  /** The whole volume, including everything else on the machine. */
  diskBusyPercent: number | null;
  diskReadBytesPerSecond: number | null;
  diskWriteBytesPerSecond: number | null;
}

export interface MonitoringDashboardSnapshot {
  generatedUtc: string;
  readiness: MonitoringReadinessSummary;
  storage: MonitoringStorageSummary;
  services: MonitoringServiceSummary;
  performance: MonitoringPerformanceSummary;
  alerts: MonitoringAlertItem[];
  /** Null before the sampler has run, or where the machine cannot be read. */
  machine: MachineTelemetrySample | null;
}

/**
 * One health check's latest result (#909) -- the same row shown on
 * System -> Health, the Dashboard tile, the bell and outbound notifications.
 */
export interface HealthCheckResultItem {
  id: string;
  category: string;
  name: string;
  status: "ok" | "problem" | "unknown" | "suggestion";
  message: string;
  cause: string | null;
  fix: string | null;
  detailsJson: string | null;
  lastRunUtc: string;
  problemSinceUtc: string | null;
  snoozedUntilUtc: string | null;
}

export interface HealthCheckSummary {
  problemCount: number;
  unsnoozedProblemCount: number;
  unknownCount: number;
  suggestionCount: number;
  lastRunUtc: string | null;
}

export interface HealthCheckListResponse {
  checks: HealthCheckResultItem[];
  summary: HealthCheckSummary;
}

/**
 * Something in a library's completed-download folder that nothing claims
 * (#588). Only what Deluno can prove it downloaded is ever returned (#1062);
 * what another tool left in a shared folder is invisible to Deluno.
 */
export interface OrphanedDownloadItem {
  path: string;
  name: string;
  libraryId: string;
  libraryName: string;
  sizeBytes: number;
  lastWrittenUtc: string;
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
  grabStatus?: string | null;
  grabAttemptedUtc?: string | null;
  grabResponseCode?: number | null;
  grabMessage?: string | null;
  grabFailureCode?: string | null;
  grabResponseJson?: string | null;
  detectedUtc?: string | null;
  torrentHashOrItemId?: string | null;
  downloadedBytes?: number | null;
  importStatus?: string | null;
  importDetectedUtc?: string | null;
  importCompletedUtc?: string | null;
  importedFilePath?: string | null;
  importFailureCode?: string | null;
  importFailureMessage?: string | null;
  circuitOpenUntilUtc?: string | null;
  attemptCount?: number | null;
  failure?: IntegrationFailure | null;
  /** The quality the release was picked for at grab time (#796). */
  decisionQuality?: string | null;
  /** The custom-format score the release was picked for at grab time (#796). */
  decisionCustomFormatScore?: number | null;
  /** The release name read for language at grab time, by the backend's own parser (#796). */
  decisionLanguage?: string | null;
  /** Why Deluno fetched it, when the server records it: you, seerr, list, upgrade or missing. Absent when it cannot be told. */
  startedBy?: string | null;
}

export interface DispatchTimelineEvent {
  id: string;
  dispatchId: string;
  eventType: string;
  timestamp: string;
  detailsJson: string | null;
  createdUtc: string;
}

export interface DownloadDispatchDetail {
  dispatch: DownloadDispatchItem;
  timeline: DispatchTimelineEvent[];
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
  /** Whether the server runs on Windows; the folder browser offers that platform's example paths only. */
  windows?: boolean;
}

export interface NativeFolderPickerResponse {
  path: string | null;
  cancelled: boolean;
}

export interface PathDiagnosticResponse {
  path: string;
  normalizedPath: string;
  root: string | null;
  exists: boolean;
  isDirectory: boolean;
  isFile: boolean;
  parentExists: boolean;
  readable: boolean;
  writable: boolean;
  isUncPath: boolean;
  isLikelyDockerPath: boolean;
  message: string;
  warnings: string[];
}

export interface LibraryViewItem {
  id: string;
  userId: string;
  variant: "movies" | "shows";
  libraryId: string | null;
  name: string;
  quickFilter: string;
  /** The monitoring axis: "any" | "monitored" | "unmonitored". Null is "any". */
  monitoring?: string | null;
  sortField: string;
  sortDirection: "asc" | "desc";
  viewMode: "grid" | "list" | "overview";
  cardSize: "sm" | "md" | "lg";
  displayOptionsJson: string;
  rulesJson: string;
  createdUtc: string;
  updatedUtc: string;
  automationAction?: "search" | null;
}

export interface MigrationImportRequest {
  sourceKind: "radarr" | "sonarr" | "prowlarr" | "recyclarr" | "custom" | string;
  sourceName: string;
  payloadJson: string;
  selectedOperationIds?: string[];
  allowAdvancedLegacyRules?: boolean;
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

export interface MigrationInventoryEntry {
  sourceKind: string;
  mediaType: string;
  category: string;
  inputRowCount: number;
  accountedRowCount: number;
  unaccountedRowCount: number;
  actionCounts: Record<string, number>;
  classificationCounts: Record<string, number>;
  warnings: string[];
  complete: boolean;
}

export interface MigrationReportInventory {
  inputRowCount: number;
  accountedRowCount: number;
  unaccountedRowCount: number;
  entries: MigrationInventoryEntry[];
}

export interface MigrationReport {
  sourceKind: string;
  sourceName: string;
  valid: boolean;
  summary: MigrationReportSummary;
  operations: MigrationReportOperation[];
  warnings: string[];
  errors: string[];
  inventory?: MigrationReportInventory | null;
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
  auditReportId?: string | null;
  backup?: MigrationBackupReceipt | null;
}

export interface MigrationBackupReceipt {
  backupId: string;
  fileName: string;
  sizeBytes: number;
  createdUtc: string;
  reason: string;
  verification: string;
}

export interface MigrationAuditReport {
  id: string;
  sourceKind: string;
  sourceName: string;
  appliedUtc: string;
  preflightReport: MigrationReport;
  resultReport: MigrationReport;
  applied: MigrationAppliedItem[];
  backup?: MigrationBackupReceipt | null;
}

export interface CreateLibraryViewRequest {
  variant: "movies" | "shows";
  libraryId: string | null;
  name: string;
  quickFilter: string;
  /** The monitoring axis: "any" | "monitored" | "unmonitored". Null is "any". */
  monitoring?: string | null;
  sortField: string;
  sortDirection: "asc" | "desc";
  viewMode: "grid" | "list" | "overview";
  cardSize: "sm" | "md" | "lg";
  displayOptionsJson: string;
  rulesJson: string;
  automationAction?: "search" | null;
}

export interface UpdateLibraryViewRequest {
  libraryId: string | null;
  name: string;
  quickFilter: string;
  /** The monitoring axis: "any" | "monitored" | "unmonitored". Null is "any". */
  monitoring?: string | null;
  sortField: string;
  sortDirection: "asc" | "desc";
  viewMode: "grid" | "list" | "overview";
  cardSize: "sm" | "md" | "lg";
  displayOptionsJson: string;
  rulesJson: string;
  automationAction?: "search" | null;
}

/** One app the installer can put on this computer, as GET /api/system/bundled-components reports it (#1061). */
export interface BundledComponentStatus {
  kind: string;
  displayName: string;
  category: "download-client" | "processor" | string;
  installed: boolean;
  reachable: boolean;
  defaultHost: string;
  defaultPort: number;
  /** Deluno already has a working connection to this app. */
  connected?: boolean;
  /** Weir only: Weir has no account yet, so the person creates one (username, password, confirm). */
  needsAccount?: boolean;
  /** Weir only: Weir already has an account, so the person signs in with it. */
  accountExists?: boolean;
}

/** What connecting an installed app did. No app's sign-in is ever part of it (#1061). */
export interface BundledComponentConnectResult {
  kind?: string;
  name: string;
  healthy: boolean;
  message: string;
  /** Weir was skipped by "Connect all": it waits for the person's own account. */
  needsAccount?: boolean;
  /** Deluno could not sign in and saved nothing: open the connection form (host and port filled in, no password). */
  needsSignIn?: boolean;
  protocol?: string | null;
  host?: string | null;
  port?: number | null;
}

/** Body of a Weir connect: the person's own account, used once and never kept. */
export interface BundledComponentConnectRequest {
  username?: string;
  password?: string;
}

/** A bundled app whose own folder was rebuilt after a reinstall (GET /api/system/component-recovery, #1177). */
export interface ComponentRecoveryNotice {
  kind: string;
  displayName: string;
  message: string;
  itemCount: number;
  canReadd: boolean;
}

export interface ComponentRecoveryResult {
  done: boolean;
  message: string;
}

export interface BundledComponentConnectAllResponse {
  results: BundledComponentConnectResult[];
}

/* Dashboard summary (GET /api/dashboard/summary, #1082)
 *
 * One compact response for every Dashboard panel, built by the server from
 * stored rows and in-memory readings only (#1081). Every section is null when
 * its source failed (and is then named in `unavailableSections`); every
 * section carries its own `asOfUtc`, because a cached reading (storage, the
 * download queue) is older than the response around it.
 */
/** One import that landed today (GET /api/dashboard/live-extras): the quality it came in at and, for an upgrade, the one it replaced. */
export interface LiveArrivalItem {
  entityType: string;
  entityId: string | null;
  release: string | null;
  quality: string | null;
  previousQuality: string | null;
  upgraded: boolean;
  sizeBytes: number | null;
  importedUtc: string;
  /** True when it landed at the quality the library asked for, false when it can still be upgraded; absent for an import recorded before this was kept. */
  qualityMet?: boolean | null;
  /** The library it landed in (#1103); absent when that library has since been removed. */
  libraryId?: string | null;
  libraryName?: string | null;
}

export interface LiveProtectionItem {
  release: string;
  reasonCode: string;
  reason: string;
  blockedUtc: string;
}

export interface LiveRequestItem {
  mediaType: string;
  entityId: string;
  title: string;
  /** added (asked for, searching), available (in the library), search-failed or available-failed. */
  status: string;
  requestedBy: string | null;
  createdUtc: string;
  updatedUtc: string;
}

/** The plain groups a turned-down release falls in (#1098). The server sends each label with it. */
export type TurnedDownGroup = "language" | "notBetter" | "notQuality" | "tooBig" | "tooSmall" | "fewSeeders" | "fake" | "rules" | "tooOld" | "blocklisted" | "other";

/** Releases Deluno looked at since the day began and did not take: the total, one entry per group (every group, zeros included, in this order) and the newest twenty. */
export interface LiveTurnedDown {
  total: number;
  reasons: Array<{ group: TurnedDownGroup; label: string; count: number }>;
  latest: Array<{
    release: string;
    group: TurnedDownGroup;
    label: string;
    title?: string | null;
    entityType?: "movie" | "series" | null;
    entityId?: string | null;
    atUtc: string;
  }>;
}

/** What else Deluno is connected to, one entry each; names are the person's own. `countToday`: processor = titles handed over, mediaServer = refreshes sent, requests = requests received, subtitles = subtitles fetched, lists = lists watched (not "today"). `busy` is a processor's titles right now. */
export interface LiveConnection {
  kind: "processor" | "mediaServer" | "requests" | "subtitles" | "lists";
  id: string;
  name: string;
  enabled: boolean;
  healthy: boolean;
  countToday: number;
  busy: number;
}

/** What the Live screen shows beyond the summary: today's arrivals and upgrades, what was refused, requests in flight, what retention will give back. */
export interface DashboardLiveExtras {
  asOfUtc: string;
  sinceUtc: string;
  arrivals: LiveArrivalItem[];
  importsToday: number;
  importedBytesToday: number;
  upgrades: LiveArrivalItem[];
  protected: { refusedToday: number; unsafeToday: number; latest: LiveProtectionItem[] };
  requests: LiveRequestItem[];
  retentionReclaimBytes: number;
  /** Looked at and not taken today (#1098). */
  turnedDown?: LiveTurnedDown | null;
  /** Finished torrents removed from a client today after they had shared enough; the library copy stays (#1098). */
  cleanedUpToday?: number;
  /** Processors, media servers, Seerr, subtitle providers and import lists (#1098). */
  connections?: LiveConnection[] | null;
  /** The newest imports of the last seven days (at most thirty, newest first), for the shelf when nothing has arrived today (#1098). */
  recentArrivals?: LiveArrivalItem[] | null;
}

export interface DashboardSummary {
  asOfUtc: string;
  buildMilliseconds: number;
  unavailableSections: string[];
  library: DashboardLibrarySection | null;
  downloads: DashboardDownloadsSection | null;
  pipeline: DashboardPipelineSection | null;
  imports: DashboardImportsSection | null;
  automation: DashboardAutomationSection | null;
  indexers: DashboardIndexersSection | null;
  processors: DashboardProcessorsSection | null;
  retention: DashboardRetentionSection | null;
  subtitles: DashboardSubtitlesSection | null;
  requests: DashboardRequestsSection | null;
  blocklist: DashboardBlocklistSection | null;
  health: DashboardHealthSection | null;
  storage: DashboardStorageSection | null;
  system: DashboardSystemSection | null;
  activity: DashboardActivitySection | null;
}

export type DashboardSectionName = Exclude<keyof DashboardSummary, "asOfUtc" | "buildMilliseconds" | "unavailableSections">;

export interface DashboardMediaCounts {
  total: number;
  monitored: number;
  unmonitored: number;
  missing: number;
  upgradable: number;
  qualityMet: number;
  upcoming: number;
  downloading: number;
  airing: number;
  fileBytes: number;
}

export interface DashboardLibraryBytes {
  id: string;
  name: string;
  mediaType: string;
  bytes: number;
}

/** One library's own counts, as its page prints them, and what arrived in it today (#1103). */
export interface DashboardLibraryCounts {
  libraryId: string;
  libraryName: string;
  mediaType: string;
  met: number;
  upgradable: number;
  downloading: number;
  missing: number;
  arrivedToday: number;
}

export interface DashboardLibrarySection {
  asOfUtc: string;
  movies: DashboardMediaCounts;
  shows: DashboardMediaCounts;
  totalBytes: number;
  libraries: DashboardLibraryBytes[];
  mix: DashboardLibraryMix;
  collections: DashboardCollections;
  /** Each library's own counts, in Settings › Libraries order (#1103). */
  libraryCounts?: DashboardLibraryCounts[] | null;
}

export interface DashboardClientRow {
  id: string;
  name: string;
  protocol: string;
  reachable: boolean;
  /** The client's own pause flag; null when its protocol has none or nothing has been read yet. */
  paused: boolean | null;
  version: string | null;
  warnings: number;
  freeSpaceBytes: number | null;
  healthStatus: string;
  active: number;
  queued: number;
}

export interface DashboardLeftovers {
  /** False until a leftover scan has run: the count is then unknown, not zero. */
  scanned: boolean;
  count: number;
  bytes: number;
  scannedUtc: string | null;
}

export interface DashboardDownloadsSection {
  asOfUtc: string;
  /** False until a download-client reading exists; the counts are then zero and mean "not measured". */
  hasReading: boolean;
  readingUtc: string | null;
  active: number;
  queued: number;
  stalled: number;
  processing: number;
  importReady: number;
  waitingForProcessor: number;
  downloadMbps: number;
  uploadMbps: number;
  clients: DashboardClientRow[];
  sharingHolds: number;
  sharingExtraBytes: number;
  leftovers: DashboardLeftovers;
  failedDownloads: number;
}

export interface DashboardPipelineSection {
  asOfUtc: string;
  today: { grabbed: number; detected: number; imported: number; failed: number };
  totals: {
    grabbed: number;
    detected: number;
    imported: number;
    failedGrabs: number;
    failedImports: number;
    activeDispatches: number;
  };
  averageGrabToDetectionSeconds: number | null;
  averageDetectionToImportSeconds: number | null;
}

export interface DashboardImportsSection {
  asOfUtc: string;
  recoveryOpen: number;
  recoveryByKind: Record<string, number>;
  failedImportJobs: number;
  failedProcessorHandoffs: number;
}

export interface DashboardQueuePauseGate {
  enabled: boolean;
  threshold: number;
  moviesPaused: boolean;
  tvPaused: boolean;
  moviesWaiting: number;
  tvWaiting: number;
  unreadable: boolean;
  unreadableReason: string | null;
}

export interface DashboardLibraryAutomation {
  libraryId: string;
  name: string;
  mediaType: string;
  status: string;
  nextSearchUtc: string | null;
  nextMissingSearchUtc: string | null;
  nextUpgradeSearchUtc: string | null;
  lastCompletedUtc: string | null;
  lastError: string | null;
}

export interface DashboardSearchCycle {
  libraryId: string;
  libraryName: string;
  mediaType: string;
  status: string;
  planned: number;
  queued: number;
  skipped: number;
  startedUtc: string;
  completedUtc: string | null;
}

export interface DashboardScheduledTask {
  key: string;
  name: string;
  lastResult: string;
  lastCompletedUtc: string | null;
  nextRunUtc: string | null;
  /** Why the last run failed, in everyday words; absent unless it failed. */
  lastError?: string | null;
}

export interface DashboardAutomationSection {
  asOfUtc: string;
  autoStartJobs: boolean;
  queuePause: DashboardQueuePauseGate;
  libraries: DashboardLibraryAutomation[];
  lastSearchCycle: DashboardSearchCycle | null;
  tasks: DashboardScheduledTask[];
}

export interface DashboardIndexersSection {
  asOfUtc: string;
  total: number;
  healthy: number;
  failing: number;
  rateLimited: number;
  disabled: number;
  queriesToday: number;
  failedQueriesToday: number;
  grabsToday: number;
  topFailing: Array<{ id: string; name: string; reason: string }>;
}

export interface DashboardProcessorsSection {
  asOfUtc: string;
  total: number;
  healthy: number;
  failing: number;
  handoffsToday: number;
  waiting: number;
}

export interface DashboardRetentionSection {
  asOfUtc: string;
  leavingSoon: number;
  nextLeaving: Array<{ entityId: string; title: string; mediaType: string; removeAfterUtc: string }>;
  readyToRemove: number;
  removedLast7Days: number;
  removedBytesLast7Days: number;
}

export interface DashboardMixEntry {
  label: string;
  count: number;
}

export interface DashboardLibraryMix {
  movieQuality: DashboardMixEntry[];
  movieCodec: DashboardMixEntry[];
  episodeQuality: DashboardMixEntry[];
  showCodec: DashboardMixEntry[];
}

export interface DashboardCollections {
  followed: number;
  monitored: number;
  members: number;
  inLibrary: number;
  syncFailing: number;
  lastSyncedUtc: string | null;
}

export interface DashboardSubtitlesSection {
  asOfUtc: string;
  wanted: number;
  held: number;
}

export interface DashboardRequestsSection {
  asOfUtc: string;
  seerrServers: number;
  seerrHealthy: number;
  seerrFailing: number;
  acquisitionsLast7Days: Record<string, number>;
  importLists: number;
  importListsByLastSync: Record<string, number>;
  listTitlesAddedLast7Days: number;
}

export interface DashboardBlocklistSection {
  asOfUtc: string;
  count: number;
}

export interface DashboardHealthProblem {
  id: string;
  title: string;
  category: string;
  message: string;
  sinceUtc: string | null;
  /** The screen that clears it, as a web path. */
  link: string;
  /** True while the person has snoozed it; a snoozed problem is not counted. */
  snoozed: boolean;
}

export interface DashboardHealthSection {
  asOfUtc: string;
  problems: number;
  unknown: number;
  suggestions: number;
  lastRunUtc: string | null;
  topProblems: DashboardHealthProblem[];
}

export interface DashboardVolume {
  id: string;
  label: string;
  /** data, library, downloads or processor. */
  roles: string[];
  paths: string[];
  /** False when the folder could not be read (offline share, missing folder). */
  available: boolean;
  totalBytes: number | null;
  freeBytes: number | null;
}

export interface DashboardStorageSection {
  asOfUtc: string;
  /** When the disks were last read in the background; null before the first reading. */
  readingUtc: string | null;
  volumes: DashboardVolume[];
}

export type DashboardLaneState = "running" | "idle" | "failing" | "restarting" | "disabled";

export interface DashboardSystemSection {
  asOfUtc: string;
  version: string;
  updateAvailable: boolean;
  latestVersion: string | null;
  uptimeSeconds: number;
  machine: { capturedUtc: string; cpuPercent: number; memoryBytes: number; memoryPercent: number | null } | null;
  jobs: { running: number; queued: number; failed: number };
  lanes: Array<{ name: string; state: DashboardLaneState; activeJobs: number; lastError: string | null }>;
  lastBackupUtc: string | null;
  nextBackupUtc: string | null;
  backupsEnabled: boolean;
  internetOutage: boolean;
  outageSinceUtc: string | null;
}

export interface DashboardActivityItem {
  id: string;
  category: string;
  /** Plain-words name of the event kind (#1086); show this, never the category code. */
  label: string;
  message: string;
  relatedEntityType: string | null;
  relatedEntityId: string | null;
  createdUtc: string;
}

export interface DashboardActivitySection {
  asOfUtc: string;
  windowHours: number;
  searchesRun: number;
  downloadsStarted: number;
  importsCompleted: number;
  cleanupRemovals: number;
  retentionRemovals: number;
  recent: DashboardActivityItem[];
}

/** A wanted title whose newest search found nothing Deluno would take (#1098). `GET /api/attention/no-release`. */
export interface NoReleaseRow {
  mediaType: "movie" | "series" | string;
  id: string;
  title: string;
  year: number | null;
  libraryId: string;
  lastSearchUtc: string;
  missingSinceUtc: string | null;
  reason: string | null;
}

/** The latest real test of one connection (#1098). `GET /api/connections/checks`, and the pushed `ConnectionChecked` event. */
export interface ConnectionCheckItem {
  kind: "indexer" | "downloadClient" | "processor" | "mediaServer" | "requests" | "metadata" | "lists" | string;
  id: string;
  name: string;
  ok: boolean;
  slow: boolean;
  message: string;
  checkedUtc: string;
}

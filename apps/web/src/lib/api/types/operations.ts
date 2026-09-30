import type { IntegrationFailure } from "./resources";

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
  idempotencyKey: string | null;
  dedupeKey: string | null;
  // The retry facts the API has always sent and this type did not name, so the
  // screens could not tell a job that will try again from one that stopped (#562).
  maxAttempts: number;
  lastAttemptUtc: string | null;
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

export interface MachineTelemetryWindow {
  hours: number;
  samples: MachineTelemetrySample[];
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

export interface BundledComponentConnectAllResponse {
  results: BundledComponentConnectResult[];
}

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
}

export interface QualityTierDefinition {
  name: string;
  rank: number;
  movieMinGb: number;
  movieMaxGb: number;
  episodeMinMb: number;
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
  nextRetryEligibleUtc?: string | null;
  attemptCount?: number | null;
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
}

export interface MigrationImportRequest {
  sourceKind: "radarr" | "sonarr" | "prowlarr" | "recyclarr" | "custom" | string;
  sourceName: string;
  payloadJson: string;
  selectedOperationIds?: string[];
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
  auditReportId?: string | null;
}

export interface MigrationAuditReport {
  id: string;
  sourceKind: string;
  sourceName: string;
  appliedUtc: string;
  preflightReport: MigrationReport;
  resultReport: MigrationReport;
  applied: MigrationAppliedItem[];
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
}

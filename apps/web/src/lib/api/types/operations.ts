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

export interface MonitoringDashboardSnapshot {
  generatedUtc: string;
  readiness: MonitoringReadinessSummary;
  storage: MonitoringStorageSummary;
  services: MonitoringServiceSummary;
  performance: MonitoringPerformanceSummary;
  alerts: MonitoringAlertItem[];
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

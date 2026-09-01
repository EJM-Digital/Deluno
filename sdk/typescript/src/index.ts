/**
 * Small, dependency-free TypeScript client for Deluno's versioned API.
 *
 * The request/response types mirror the shipped OpenAPI contract. Keep this
 * client deliberately boring: it is suitable for Node, a browser, or a
 * Home-Assistant companion process and does not depend on the Deluno UI.
 */

export type DelunoMediaType = "movie" | "movies" | "tv" | "series";

export interface DelunoClientOptions {
  baseUrl: string;
  apiKey: string;
  fetch?: typeof globalThis.fetch;
}

export interface BulkCatalogueEpisode {
  seasonNumber: number;
  episodeNumber: number;
  title?: string;
  overview?: string;
  airDateUtc?: string;
  absoluteNumber?: number;
  sceneSeasonNumber?: number;
  sceneEpisodeNumber?: number;
  numberingSource?: "provider" | "owner" | string;
}

export interface BulkCatalogueAddItem {
  clientItemId?: string;
  mediaType: DelunoMediaType;
  title: string;
  year?: number;
  imdbId?: string;
  libraryId?: string;
  monitored?: boolean;
  isReleased?: boolean;
  metadataProvider?: string;
  metadataProviderId?: string;
  originalTitle?: string;
  overview?: string;
  posterUrl?: string;
  backdropUrl?: string;
  rating?: number;
  genres?: string;
  externalUrl?: string;
  metadataJson?: string;
  seriesType?: "standard" | "daily" | "anime" | string;
  numberingScheme?: "standard" | "airdate" | "absolute" | "scene" | string;
  numberingSource?: "provider" | "owner" | string;
  episodes?: BulkCatalogueEpisode[];
}

export interface BulkCatalogueAddRequest {
  items: BulkCatalogueAddItem[];
  dryRun?: boolean;
  idempotencyKey?: string;
}

export interface BulkCatalogueItemResult {
  clientItemId: string;
  mediaType: string;
  title?: string;
  status: "would-create" | "created" | "already-exists" | "invalid" | "failed" | string;
  entityId?: string;
  error?: string;
  episodeCount: number;
  episodesAdded: number;
  episodesUpdated: number;
  refreshJobId?: string;
}

export interface BulkCatalogueAddResponse {
  dryRun: boolean;
  idempotencyKey?: string;
  total: number;
  createdCount: number;
  existingCount: number;
  invalidCount: number;
  failedCount: number;
  items: BulkCatalogueItemResult[];
}

export interface BulkSeriesEpisodeItem {
  clientItemId?: string;
  seasonNumber: number;
  episodeNumber: number;
  title?: string;
  overview?: string;
  airDateUtc?: string;
  absoluteNumber?: number;
  sceneSeasonNumber?: number;
  sceneEpisodeNumber?: number;
  numberingSource?: "provider" | "owner" | string;
}

export interface BulkSeriesEpisodeRequest {
  episodes: BulkSeriesEpisodeItem[];
  dryRun?: boolean;
  idempotencyKey?: string;
}

export interface BulkSeriesEpisodeItemResult {
  clientItemId: string;
  seasonNumber: number;
  episodeNumber: number;
  status: "would-sync" | "synced" | "invalid" | "failed" | string;
  error?: string;
}

export interface BulkSeriesEpisodeResponse {
  dryRun: boolean;
  idempotencyKey?: string;
  seriesId: string;
  total: number;
  syncedCount: number;
  invalidCount: number;
  failedCount: number;
  episodesAdded: number;
  episodesUpdated: number;
  episodes: BulkSeriesEpisodeItemResult[];
}

export interface DelunoScopeTemplate {
  id: string;
  name: string;
  description: string;
  scopes: string[];
  capabilities: string[];
}

export type ReleasePreferenceIntent = "required" | "forbidden" | "ranked" | "tieBreak" | "neutral";

export interface ReleasePreferenceFamilyLevel {
  id: string;
  rank: number;
  traitIds: string[];
}

export interface ReleasePreferenceFamily {
  id: string;
  dimension: string;
  order: number;
  intent: ReleasePreferenceIntent;
  levels: ReleasePreferenceFamilyLevel[];
  targetLevelId?: string | null;
  upgradeDriving?: boolean;
  transient?: boolean;
}

export interface ReleasePreferencePlan {
  id: string;
  version: string;
  mediaType: string;
  families: ReleasePreferenceFamily[];
  requiredTraitIds?: string[] | null;
  requiredAnyTraitGroups?: string[][] | null;
  compatibilityGroups?: Array<{
    id: string;
    alternatives: string[][];
  }> | null;
  forbiddenTraitIds?: string[] | null;
  relationships?: Array<{ fromTraitId: string; toTraitId: string; kind: string }> | null;
  dimensionOrder?: string[] | null;
  compatibilityScope?: string | null;
  scenario?: string | null;
  provenance?: string | null;
  overrides?: Record<string, string> | null;
  sources?: Array<Record<string, string | null>> | null;
}

export interface StoredReleasePreferencePlan {
  plan: ReleasePreferencePlan;
  planHash: string;
  createdUtc: string;
}

export type PreferenceFactState = "present" | "absent" | "unknown" | "conflicting";
export type PreferenceEvaluationStatus = "missing" | "needsReview" | "belowGoal" | "meetsPlan";
export type PreferenceCandidateStatus =
  | "rejected"
  | "needsReview"
  | "acceptable"
  | "bestMatchNow"
  | "equivalent"
  | "upgrade";
export type PreferenceEvidenceModel = "openWorld" | "closedWorld";

export interface PreferenceEvidence {
  source: string;
  confidence?: number | null;
  detail?: string | null;
  detectionRule?: string | null;
  detectionVersion?: string | null;
  model: PreferenceEvidenceModel;
}

export interface PreferenceFact {
  traitId: string;
  state: PreferenceFactState;
  evidence?: PreferenceEvidence | null;
}

export interface PreferenceFamilyEvaluation {
  familyId: string;
  intent: ReleasePreferenceIntent;
  state: PreferenceFactState;
  selectedLevelId?: string | null;
  selectedRank: number;
  targetLevelId?: string | null;
  targetMet: boolean;
  upgradeDriving: boolean;
  transient: boolean;
  explanation: string;
}

export interface PreferenceEvaluation {
  planId: string;
  planVersion: string;
  planHash: string;
  status: PreferenceEvaluationStatus;
  hardGatesPassed: boolean;
  targetsMet: boolean;
  families: PreferenceFamilyEvaluation[];
  reasons: string[];
}

export interface PreferenceComparison {
  planId: string;
  planVersion: string;
  planHash: string;
  status: PreferenceCandidateStatus;
  persistentImprovement: boolean;
  regressed: boolean;
  equivalent: boolean;
  decisiveFamilyId?: string | null;
  persistentImprovementFamilyId?: string | null;
  reasons: string[];
  current: PreferenceEvaluation;
  candidate: PreferenceEvaluation;
}

export interface ReleasePreferencePreviewRequest {
  planId?: string | null;
  planVersion?: string | null;
  releaseName: string;
  currentReleaseName?: string | null;
  candidateQuality?: string | null;
  currentQuality?: string | null;
  seeders?: number | null;
  candidateFacts?: PreferenceFact[] | null;
  currentFacts?: PreferenceFact[] | null;
}

export interface ReleasePreferencePreviewResponse {
  releaseName: string;
  planId: string;
  planVersion: string;
  planHash: string;
  candidateFacts: PreferenceFact[];
  candidateEvaluation: PreferenceEvaluation;
  currentReleaseName?: string | null;
  currentFacts?: PreferenceFact[] | null;
  currentEvaluation?: PreferenceEvaluation | null;
  comparison?: PreferenceComparison | null;
}

export interface AutomationSummaryResponse {
  generatedUtc: string;
  readiness: {
    status: string;
    ready: boolean;
    failedChecks: number;
  };
  queue: {
    active: number;
    queued: number;
    failed: number;
    openDispatchAlerts: number;
  };
  imports: {
    active: number;
    failed: number;
    completed: number;
    issues: number;
  };
  attention: Array<{
    code: string;
    severity: string;
    summary: string;
    details: string;
    detectedUtc: string;
  }>;
}

export type NotificationWebhookDeliveryStatus = "pending" | "retrying" | "delivered" | "dead-letter" | string;

export interface IntegrationFailure {
  serviceType: string;
  serviceId: string;
  serviceName: string;
  operation: string;
  kind: string;
  retryState: string;
  message: string;
  code?: string | null;
  httpStatus?: number | null;
  upstreamDetail?: string | null;
  externalId?: string | null;
  retryAfterUtc?: string | null;
  attempts: number;
  isTransient: boolean;
  legacyCategory: string;
  summary: string;
  nextAction: string;
}

export interface NotificationWebhookDeliveryItem {
  id: string;
  webhookId: string;
  eventCategory: string;
  title: string;
  status: NotificationWebhookDeliveryStatus;
  attemptCount: number;
  maxAttempts: number;
  nextAttemptUtc?: string | null;
  lastAttemptUtc?: string | null;
  lastStatusCode?: number | null;
  lastError?: string | null;
  createdUtc: string;
  updatedUtc: string;
  failure?: IntegrationFailure | null;
}

export interface NotificationWebhookDeliveryResult {
  sent: boolean;
  deliveryId?: string | null;
  status: string;
  attempts: number;
  error?: string | null;
  failure?: IntegrationFailure | null;
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
  notesJson?: string | null;
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
  failure?: IntegrationFailure | null;
}

export interface DispatchTimelineEvent {
  id: string;
  dispatchId: string;
  eventType: string;
  timestamp: string;
  detailsJson?: string | null;
  createdUtc: string;
}

export interface DownloadDispatchListResponse {
  items: DownloadDispatchItem[];
  nextPageToken?: string | null;
  hasMore: boolean;
}

export interface DownloadDispatchDetail {
  dispatch: DownloadDispatchItem;
  timeline: DispatchTimelineEvent[];
}

export interface ImportResolutionItem {
  id: string;
  dispatchId: string;
  entityId: string;
  mediaType: string;
  libraryId: string;
  status: string;
  filePath?: string | null;
  fileName?: string | null;
  fileSize?: number | null;
  importedUtc?: string | null;
  failureCode?: string | null;
  failureMessage?: string | null;
  failedUtc?: string | null;
  failure?: IntegrationFailure | null;
}

export interface ImportResolutionListResponse {
  items: ImportResolutionItem[];
  nextPageToken?: string | null;
  hasMore: boolean;
}

export interface DelunoPage<T> {
  items: T[];
  nextPageToken?: string | null;
  hasMore?: boolean;
}

export interface DelunoApiErrorBody {
  error?: string;
  message?: string;
  errors?: Record<string, string[]>;
}

export class DelunoApiError extends Error {
  readonly status: number;
  readonly body: DelunoApiErrorBody | undefined;

  constructor(status: number, body: DelunoApiErrorBody | undefined) {
    super(body?.message ?? body?.error ?? `Deluno API request failed (${status}).`);
    this.name = "DelunoApiError";
    this.status = status;
    this.body = body;
  }
}

export class DelunoClient {
  private readonly baseUrl: string;
  private readonly apiKey: string;
  private readonly transport: typeof globalThis.fetch;

  constructor(options: DelunoClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/$/, "");
    this.apiKey = options.apiKey;
    this.transport = options.fetch ?? globalThis.fetch;
  }

  async readiness(): Promise<unknown> {
    return this.request("/health/ready");
  }

  async scopeTemplates(): Promise<DelunoScopeTemplate[]> {
    return this.request("/api-keys/scope-templates");
  }

  async automationSummary(): Promise<AutomationSummaryResponse> {
    return this.request("/automation/summary");
  }

  async listNotificationWebhookDeliveries(options: {
    status?: NotificationWebhookDeliveryStatus;
    webhookId?: string;
    take?: number;
  } = {}): Promise<NotificationWebhookDeliveryItem[]> {
    const query = new URLSearchParams();
    if (options.status) query.set("status", options.status);
    if (options.webhookId) query.set("webhookId", options.webhookId);
    if (options.take !== undefined) query.set("take", String(options.take));
    const suffix = query.toString() ? `?${query.toString()}` : "";
    return this.request(`/notification-webhooks/deliveries${suffix}`);
  }

  async replayNotificationWebhookDelivery(deliveryId: string): Promise<NotificationWebhookDeliveryResult> {
    return this.request(`/notification-webhooks/deliveries/${encodeURIComponent(deliveryId)}/replay`, {
      method: "POST",
    });
  }

  async listDownloadDispatches(options: {
    grabStatus?: string;
    importStatus?: string;
    clientId?: string;
    mediaType?: string;
    entityType?: string;
    entityId?: string;
    libraryId?: string;
    minGrabTime?: string;
    maxGrabTime?: string;
    minImportTime?: string;
    maxImportTime?: string;
    pageSize?: number;
    pageToken?: string;
  } = {}): Promise<DownloadDispatchListResponse> {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(options)) {
      if (value !== undefined) query.set(key, String(value));
    }
    const suffix = query.toString() ? `?${query.toString()}` : "";
    return this.request(`/download-dispatches${suffix}`);
  }

  async getDownloadDispatch(dispatchId: string): Promise<DownloadDispatchDetail> {
    return this.request(`/download-dispatches/${encodeURIComponent(dispatchId)}`);
  }

  async listImportResolutions(options: {
    status?: string;
    libraryId?: string;
    mediaType?: string;
    entityId?: string;
    importedAfter?: string;
    importedBefore?: string;
    pageSize?: number;
    pageToken?: string;
  } = {}): Promise<ImportResolutionListResponse> {
    const query = new URLSearchParams();
    for (const [key, value] of Object.entries(options)) {
      if (value !== undefined) query.set(key, String(value));
    }
    const suffix = query.toString() ? `?${query.toString()}` : "";
    return this.request(`/import-resolutions${suffix}`);
  }

  async listReleasePreferencePlans(mediaType?: string): Promise<StoredReleasePreferencePlan[]> {
    const query = mediaType ? `?mediaType=${encodeURIComponent(mediaType)}` : "";
    return this.request(`/release-preferences/plans${query}`);
  }

  async getReleasePreferencePlan(planId: string, version?: string): Promise<StoredReleasePreferencePlan> {
    const query = version ? `?version=${encodeURIComponent(version)}` : "";
    return this.request(`/release-preferences/plans/${encodeURIComponent(planId)}${query}`);
  }

  async saveReleasePreferencePlan(plan: ReleasePreferencePlan): Promise<StoredReleasePreferencePlan> {
    return this.request("/release-preferences/plans", {
      method: "POST",
      body: plan,
    });
  }

  async previewReleasePreference(
    request: ReleasePreferencePreviewRequest,
  ): Promise<ReleasePreferencePreviewResponse> {
    return this.request("/release-preferences/preview", {
      method: "POST",
      body: request,
    });
  }

  async bulkAddCatalogue(
    request: BulkCatalogueAddRequest,
  ): Promise<BulkCatalogueAddResponse> {
    return this.request("/automation/catalogue/bulk", {
      method: "POST",
      body: request,
      idempotencyKey: request.idempotencyKey,
    });
  }

  async syncSeriesEpisodes(
    seriesId: string,
    request: BulkSeriesEpisodeRequest,
  ): Promise<BulkSeriesEpisodeResponse> {
    return this.request(`/automation/series/${encodeURIComponent(seriesId)}/episodes/bulk`, {
      method: "POST",
      body: request,
      idempotencyKey: request.idempotencyKey,
    });
  }

  async listJobs(pageSize = 50, pageToken?: string): Promise<DelunoPage<unknown>> {
    const query = new URLSearchParams({ pageSize: String(pageSize) });
    if (pageToken) query.set("pageToken", pageToken);
    return this.request(`/jobs?${query}`);
  }

  async listActivity(pageSize = 50, pageToken?: string): Promise<DelunoPage<unknown>> {
    const query = new URLSearchParams({ pageSize: String(pageSize) });
    if (pageToken) query.set("pageToken", pageToken);
    return this.request(`/activity?${query}`);
  }

  async listDecisions(pageSize = 50, pageToken?: string): Promise<DelunoPage<unknown>> {
    const query = new URLSearchParams({ pageSize: String(pageSize) });
    if (pageToken) query.set("pageToken", pageToken);
    return this.request(`/decisions?${query}`);
  }

  async listBackups(): Promise<unknown[]> {
    return this.request("/backups");
  }

  async searchLibrary(libraryId: string): Promise<unknown> {
    return this.request(`/libraries/${encodeURIComponent(libraryId)}/search-now`, { method: "POST" });
  }

  async pauseExistingLibraryImport(libraryId: string): Promise<unknown> {
    return this.request(`/libraries/${encodeURIComponent(libraryId)}/import-existing/pause`, { method: "POST" });
  }

  async resumeExistingLibraryImport(libraryId: string): Promise<unknown> {
    return this.request(`/libraries/${encodeURIComponent(libraryId)}/import-existing/resume`, { method: "POST" });
  }

  async startExistingLibraryImport(libraryId: string): Promise<unknown> {
    return this.request(`/libraries/${encodeURIComponent(libraryId)}/import-existing`, { method: "POST" });
  }

  async importProgress(libraryId: string): Promise<unknown> {
    return this.request(`/libraries/${encodeURIComponent(libraryId)}/import-existing`);
  }

  async setGlobalAutomationEnabled(isEnabled: boolean): Promise<unknown> {
    return this.request("/settings/automation", {
      method: "PUT",
      body: { isEnabled },
    });
  }

  async createBackup(reason = "automation"): Promise<unknown> {
    return this.request("/backups", {
      method: "POST",
      body: { reason },
    });
  }

  async approveIntakePreview(sourceId: string, body: unknown): Promise<unknown> {
    return this.request(`/intake-sources/${encodeURIComponent(sourceId)}/approve-preview`, {
      method: "POST",
      body,
    });
  }

  private async request<T>(
    path: string,
    options: { method?: string; body?: unknown; idempotencyKey?: string } = {},
  ): Promise<T> {
    const headers: Record<string, string> = {
      Accept: "application/json",
      "X-Api-Key": this.apiKey,
    };
    if (options.body !== undefined) headers["Content-Type"] = "application/json";
    if (options.idempotencyKey) headers["Idempotency-Key"] = options.idempotencyKey;

    const response = await this.transport(`${this.baseUrl}/api/v1${path}`, {
      method: options.method ?? "GET",
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
    });
    const text = await response.text();
    let body: T | DelunoApiErrorBody | undefined;
    if (text) {
      try {
        body = JSON.parse(text) as T | DelunoApiErrorBody;
      } catch {
        body = { message: text };
      }
    }
    if (!response.ok) {
      throw new DelunoApiError(response.status, body as DelunoApiErrorBody | undefined);
    }
    return body as T;
  }
}

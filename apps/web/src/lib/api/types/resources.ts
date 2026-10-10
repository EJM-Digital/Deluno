import type { JobQueueItem, QualityUpgradeStopPolicy } from "./operations";

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
  /** The processor connection this library hands downloads to; renaming the connection does not change it. */
  processorConnectionId?: string | null;
  processorOutputPath: string | null;
  processorTimeoutMinutes: number;
  processorFailureMode: "block" | "import-original" | "manual-review" | string;
  cleanupMode?: "keep-source" | "remove-source-after-import" | string;
  removeEmptySourceFolders?: boolean;
  autoSearchEnabled: boolean;
  missingSearchEnabled: boolean;
  upgradeSearchEnabled: boolean;
  searchIntervalMinutes: number;
  retryDelayHours: number;
  maxItemsPerRun: number;
  /** How many missing-item candidates a search cycle works through, independent of maxUpgradeItemsPerRun (#830). */
  maxMissingItemsPerRun?: number;
  /** How many cutoff-unmet (upgrade) candidates a search cycle works through, independent of maxMissingItemsPerRun (#830). */
  maxUpgradeItemsPerRun?: number;
  searchWindowStartHour: number | null;
  searchWindowEndHour: number | null;
  /** Which days of the week the search window applies on, as a 7-bit mask (Sunday = bit 0 ... Saturday = bit 6). 127 is every day (#832). */
  searchWindowDays?: number;
  /** Shuffles automatic wanted-title search order instead of oldest-added first, so a recent add is never stuck behind a long backlog. */
  randomizeSearchOrder?: boolean;
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
  subtitlePictureBasedCounts?: boolean;
  /** Named post-download subtitle cleanups. */
  subtitleContentPolicy?: SubtitleContentModificationPolicy | null;
  /** Automatic timing repair policy for fetched subtitles. */
  subtitleTimingPolicy?: SubtitleTimingPolicy | null;
  /** Words a subtitle's release name must or must not carry. */
  subtitleNamePolicy?: SubtitleNamePolicy | null;
  /** Whether a written subtitle leaves the language code out of its name. */
  subtitleOmitLanguageCode?: boolean;
  /**
   * Per-language hearing-impaired and forced-track preferences (#835, #836,
   * #923). A wanted language with no row here gets the defaults — hearing-
   * impaired is coverage with a plain track winning a tie, and forced is
   * never fetched.
   */
  subtitleLanguageTypePreferences?: SubtitleLanguageTypePreference[] | null;
}

/** Bazarr's per-language required/not-required/only, in Deluno's own words. */
export type SubtitleHearingImpairedPreference = "plain-only" | "plain-preferred" | "hi-preferred";

export interface SubtitleLanguageTypePreference {
  language: string;
  hearingImpaired: SubtitleHearingImpairedPreference | string;
  /** Also fetch a forced track for this language as an extra file, never counted as coverage. */
  includeForced: boolean;
}

/** One subtitle a title actually has, and which of the three types it is (#923). */
export interface MediaSubtitleRow {
  language: string;
  source: "fetched" | "external" | "embedded" | string;
  forced: boolean;
  hearingImpaired: boolean;
  filePath: string | null;
  streamIndex: number | null;
  codec: string | null;
  provider: string | null;
  sourceKey: string | null;
  /**
   * What the last timing sync found — `"moved"`, `"already_in_time"`, or
   * `"not_checked"` when it could not decide either way (#922). Null for a
   * subtitle no sync has run against yet.
   */
  timingStatus?: "moved" | "already_in_time" | "not_checked" | string | null;
  /** The one-sentence reason behind `timingStatus`. */
  timingReason?: string | null;
}

export interface SubtitleNamePolicy {
  mustContain: string[] | null;
  mustNotContain: string[] | null;
}

export interface SubtitleContentModificationPolicy {
  stripHearingImpairedAnnotations: boolean;
  removeStyleTags: boolean;
  removeEmoji: boolean;
  normalizeWhitespace: boolean;
  fixAllUppercase: boolean;
  /** Repairs a lone l read as I, and 0 or 1 read inside a word. */
  fixOcrErrors: boolean;
  /** A CSS colour name or hex value, or null to leave cues uncoloured. */
  cueColour: string | null;
  /** Moves trailing punctuation to the front of a right-to-left line. */
  reverseRightToLeftPunctuation: boolean;
}

export interface SubtitleTimingPolicy {
  enabled: boolean;
  syncOnlyBelow: "same-source" | "made-for-this-file" | string;
  maxOffsetSeconds: number;
  requiredPeakSigma: number;
  excludedProviders: string[] | null;
  repairFramerate: boolean;
}

/** One language Deluno can name, from GET /api/subtitle-languages. */
export interface SubtitleLanguageOption {
  code: string;
  name: string;
}

/**
 * One candidate a manual subtitle search found (#841) — ranked, never
 * downloaded until picked. `downloadToken` and `providerKey` together
 * identify it for both `candidates/pick` and `blacklist`.
 */
export interface SubtitleSearchCandidateItem {
  providerKey: string;
  providerName: string;
  blockKey: string;
  downloadToken: string;
  language: string;
  hearingImpaired: boolean;
  releaseName: string | null;
  uploader: string | null;
  downloadCount: number | null;
  match: number;
  matchDescription: string;
}

/**
 * One row of a title's append-only subtitle search history (#840) — the
 * same data the Wanted screen's status reads from, shown from the title's
 * own subtitle panel: "what has Deluno already tried".
 */
export interface MediaSubtitleAttemptHistoryEntry {
  language: string;
  provider: string | null;
  outcome: string;
  attemptedUtc: string;
  /**
   * The rung of the best candidate this attempt found (#982) — the same
   * number a held subtitle's own match carries. Null when nothing was
   * found, so there was no candidate to score.
   */
  score: number | null;
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
  /** This profile's own size answers. Empty means it has no size opinion. */
  sizeRules?: ProfileSizeRule[] | null;
  /** When this profile stops looking for something better. */
  upgradeStop?: QualityUpgradeStopPolicy | null;
  /** How much this profile cares about each preference it selected, by format id. */
  formatIntents?: Record<string, string> | null;
  /** How this profile wants a release fetched, and the words it insists on or refuses. */
  acquisition?: ProfileAcquisitionRules | null;
  /** The minimum, cutoff and minimum-upgrade-gain score guardrails behind the plain choices. */
  scoreLimits?: ProfileScoreLimits | null;
  /** "Preferred language" for this profile. */
  languagePreference?: ProfileLanguagePreference | null;
  createdUtc: string;
  updatedUtc: string;
}

/**
 * The TRaSH-style score guardrails behind "Stop upgrading once a copy is",
 * "Only replace when clearly better" and "Never grab copies rated bad"
 * (#778). Every field is null when the profile has no opinion.
 */
export interface ProfileScoreLimits {
  minFormatScore?: number | null;
  cutoffFormatScore?: number | null;
  minUpgradeFormatScore?: number | null;
}

/** "Preferred language" for a quality profile (#769). */
export interface ProfileLanguagePreference {
  /** Best-first. Empty means no language opinion. */
  preferredLanguages: string[];
  /** True: refuse a release explicitly tagged as a different language. */
  requireLanguage: boolean;
}

/**
 * How big a file of one tier should be, for one profile. #394: nothing in
 * Quality & Release is shared, because anime at 1080p and a film at 1080p are
 * not the same number of gigabytes.
 */
/**
 * How a profile wants a release fetched. #394: this used to be keyed by tag, so
 * protocol preference and delays sat apart from the seven answers they belong
 * beside.
 */
export interface ProfileAcquisitionRules {
  preferredProtocol: "any" | "usenet" | "torrent" | string;
  usenetDelayMinutes: number;
  torrentDelayMinutes: number;
  /** Comma-separated words a release name must carry. */
  mustContain: string;
  /** Comma-separated words a release name must not carry. */
  mustNotContain: string;
}

export interface ProfileSizeRule {
  quality: string;
  minGb: number;
  maxGb: number;
  minMb: number;
  maxMb: number;
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
  /** Connections and rules that name this tag (#1039). */
  users: TagUserItem[];
  isUnused: boolean;
}

export interface TagUserItem {
  kind: string;
  id: string;
  name: string;
  /** The page where this is set. */
  link: string;
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
  /** Minimum release stage (movies only) a list-added title must reach before Deluno searches for it (#849). */
  minimumAvailability?: "announced" | "inCinemas" | "released" | string;
  /** How much of a newly added title to actually watch for (#848): All/Future/Missing/Existing/Recent for TV, Movie/None for movies. */
  monitorMode?: "all" | "future" | "missing" | "existing" | "recent" | "movie" | "none" | string;
  /** TV only (#850): Standard/Daily/Anime, mirroring the same choice on a direct series add. */
  seriesType?: "standard" | "daily" | "anime" | string | null;
  /** TV only (#850): true/false forces a season subfolder on or off; null/unset follows the global Media Management format. */
  seasonFolder?: boolean | null;
  /** #1015: comma-separated tag names given to every title this list adds. */
  tags?: string;
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

/** Trakt OAuth connection state (#846): a real sign-in, not scraping the public RSS feed. */
export interface TraktConnectionStatus {
  clientConfigured: boolean;
  connected: boolean;
  expiresUtc: string | null;
  needsReconnect: boolean;
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
  /** #1033: the library this rule routes a title to, when it is used for library routing (e.g. by Seerr) rather than only folder placement. */
  libraryId: string | null;
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
  /** The grab behind the file, so the preview names it as the title grabbed (#617). */
  dispatchId?: string | null;
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
  /** The show in the library this file was matched to (#1171). */
  matchedTitle?: string | null;
  /** A TV file matched nothing and no title was given: the person is asked for one (#1171). */
  needsTitle?: boolean;
  /** A connected download client still holds this file, so importing it copies and leaves the original (#1171). */
  clientHoldsSource?: boolean;
}

export interface ImportPackPreview {
  canExecute: boolean;
  alreadyCommitted: boolean;
  sourceFileCount: number;
  episodeCount: number;
  files: ImportPackFilePreview[];
  blockReasons: string[];
  /** Files in the pack folder that do not resolve to a canonical episode: extras, featurettes, samples, or specials the catalogue does not list. */
  skippedFiles?: ImportPackSkippedFile[] | null;
}

export interface ImportPackFilePreview {
  sourcePath: string;
  destinationPath: string;
  sourceSizeBytes: number;
  episodeKeys: string[];
  warnings: string[];
}

export interface ImportPackSkippedFile {
  sourcePath: string;
  reason: string;
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
  skippedFiles?: ImportPackSkippedFile[] | null;
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

/** #783: manual import scans a folder rather than one file at a time. */
export interface ImportFolderScanRequest {
  folderPath: string;
  mediaType?: string | null;
  libraryId?: string | null;
  seriesId?: string | null;
}

export interface ImportFolderScanItem {
  sourcePath: string;
  fileName: string;
  guessedTitle: string | null;
  guessedYear: number | null;
  /** Whether Deluno could guess anything at all — an unmatched row still appears, flagged for a person to fill in rather than silently dropped. */
  matched: boolean;
  preview: ImportPreviewResponse;
}

export interface ImportFolderScanResponse {
  folderPath: string;
  items: ImportFolderScanItem[];
  issues: string[];
}

export interface ImportBatchExecuteResult {
  sourcePath: string;
  succeeded: boolean;
  message: string;
  response: ImportExecuteResponse | null;
}

export interface ImportBatchExecuteResponse {
  requestedCount: number;
  importedCount: number;
  results: ImportBatchExecuteResult[];
}

/** #785: Discover — TMDb's own "popular" list plus every configured import list's entries. */
export interface DiscoverItem {
  provider: string;
  providerId: string;
  title: string;
  year: number | null;
  overview: string | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  rating: number | null;
  imdbId: string | null;
  externalUrl: string | null;
  inLibrary: boolean;
  libraryEntryId: string | null;
  excluded: boolean;
}

export interface DiscoverListEntry {
  title: string;
  year: number | null;
  imdbId: string | null;
  status: string;
  reason: string;
  inLibrary: boolean;
  excluded: boolean;
}

export interface DiscoverListSection {
  sourceId: string;
  sourceName: string;
  items: DiscoverListEntry[];
}

export interface DiscoverResponse {
  popular: DiscoverItem[];
  lists: DiscoverListSection[];
}

export interface DiscoverAddRequest {
  mediaType: string;
  title: string;
  year: number | null;
  imdbId: string | null;
  provider: string | null;
  providerId: string | null;
  overview: string | null;
  posterUrl: string | null;
  backdropUrl: string | null;
  libraryId: string | null;
  qualityProfileId: string | null;
  monitored: boolean;
  /** Search for this title's missing content right away, once it is added from Discover (#785, mirroring #793). Off by default. */
  searchForMissingNow?: boolean;
  /** Search for this title's cutoff-unmet content right away, once it is added from Discover (#785, mirroring #793). Off by default. */
  searchForCutoffUnmetNow?: boolean;
}

export interface DiscoverAddResponse {
  id: string;
  mediaType: string;
  libraryId: string | null;
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
  kind: "Authentication" | "RateLimit" | "Timeout" | "Protocol" | "Unavailable" | "MalformedResponse" | "RejectedAction" | "Configuration" | "CircuitOpen" | "QuotaExceeded" | "Unknown" | string;
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

/** The state of an indexer's query or grab cap at read time (#977). Mirrors IndexerCallLimiter.Peek. */
export interface IndexerCallLimitStatus {
  allowed: boolean;
  limit: number | null;
  count: number;
  resetsAtUtc: string | null;
  unit: string | null;
}

export interface IndexerItem {
  id: string;
  name: string;
  /** Saved on another computer and cannot be read here (#1094): listed as missing until entered again. */
  secretUnreadable?: boolean;
  secretProblem?: string | null;

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
  /** A non-default Newznab/Torznab API path, such as /api/v2 (#740). Null or blank uses /api. */
  apiPath?: string | null;
  /** Extra Newznab/Torznab query parameters some indexers require (#741). */
  additionalParameters?: string | null;
  /** This indexer's own anime category numbers (#742). Null uses categories for anime too. */
  animeCategories?: string | null;
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
  /**
   * A separate seed time, in hours, used instead of sharingForHours when the
   * completed download is a season pack rather than a single episode (#739).
   * Null falls back to the normal seed time.
   */
  seasonPackSeedForHours?: number | null;
  minimumAgeMinutes?: number | null;
  retentionDays?: number | null;
  maximumSizeMb?: number | null;
  /** The seeder floor a torrent from this indexer must clear to be grabbed (#738). Null means unrestricted. */
  minimumSeeders?: number | null;
  preferIndexerFlags?: string | null;
  /** Comma-separated, case-insensitive words a release's title must contain to be grabbed from this indexer (#744). */
  requiredWords?: string | null;
  /** Comma-separated, case-insensitive words that reject a release from this indexer before grab (#744). */
  ignoredWords?: string | null;
  /** What this indexer's own MULTI tag means, e.g. "Original, French" (#746). Null uses Deluno's fixed "Multilingual" default. */
  multiLanguages?: string | null;
  /** The recorded VIP/subscription expiry date for a private-tracker indexer (#747). Null means nothing is tracked. */
  vipExpiresUtc?: string | null;
  /** Which vipExpiresUtc value the user last acknowledged a warning for (#747). */
  vipWarningAcknowledgedExpiryUtc?: string | null;
  /** A hard ceiling on searches sent to this indexer per limitsUnit (#750), in addition to requestIntervalSeconds' pacing. Null means unrestricted. */
  queryLimit?: number | null;
  /** A hard ceiling on grabs sent to this indexer per limitsUnit (#750). Null means unrestricted. */
  grabLimit?: number | null;
  /** "day" (default) or "hour" — the window queryLimit and grabLimit count against (#750). */
  limitsUnit?: string | null;
  /** The current state of queryLimit, computed at read time so Health can show usage and reset time (#977). Null when no query cap is set. */
  queryLimitStatus?: IndexerCallLimitStatus | null;
  /** The current state of grabLimit (#977). Null when no grab cap is set. */
  grabLimitStatus?: IndexerCallLimitStatus | null;
  /** For a torrent indexer, prefer a magnet URL over a .torrent file link when a grab has both (#752). Ignored for usenet. */
  preferMagnetUrl?: boolean;
  /** Restricts TV searches against this indexer to season packs only, episodes only, or both (default) (#743). */
  seasonPackSearchMode?: "both" | "season-packs-only" | "episodes-only" | null;
  availabilityDelayDays?: number | null;
  rssEnabled?: boolean;
  automaticSearchEnabled?: boolean;
  interactiveSearchEnabled?: boolean;
  /** The built-in tracker a "tracker" indexer runs (#606). */
  definitionId?: string | null;
  /** That tracker's settings that are not secret, by setting name. */
  definitionSettings?: Record<string, string> | null;
  /** Which of its secrets (password, cookie, key) are saved. The values never leave Deluno. */
  definitionSecretsSet?: string[];
  /** The configured proxy (#748) this indexer's requests go through; null is a direct connection. */
  proxyId?: string | null;
}

/** A configured indexer proxy (#748): HTTP, SOCKS4, SOCKS5 or FlareSolverr/Byparr. */
export interface ProxyItem {
  id: string;
  name: string;
  proxyType: "http" | "socks4" | "socks5" | "flaresolverr";
  address: string;
  username?: string | null;
  hasPassword: boolean;
  /** Saved on another computer and cannot be read here (#1094): listed as missing until entered again. */
  secretUnreadable?: boolean;
  secretProblem?: string | null;
  isEnabled: boolean;
  createdUtc: string;
  updatedUtc: string;
  /** Names of the indexers currently assigned to this proxy. */
  assignedIndexerNames: string[];
}

/** One tracker Deluno can search itself, from GET /api/trackers (#606). */
export interface TrackerDefinitionSummary {
  id: string;
  name: string;
  description: string;
  language: string;
  /** public, semi-private or private. */
  privacy: string;
  links: string[];
  movies: boolean;
  tv: boolean;
  loginMethod: string | null;
  settings: TrackerSettingSummary[];
  mayNeedCloudflareHelp: boolean;
  /** Why the engine cannot run it, or null when it can. */
  unsupported: string | null;
  /** bundled (the built-in Prowlarr site list), deluno (a Deluno-written file kept over the list's) or data-folder (a newer update or a file put there). */
  origin: string;
}

export interface TrackerSettingSummary {
  name: string;
  /** text, password, checkbox, select, multi-select or info. */
  type: string;
  label: string;
  default: string | null;
  secret: boolean;
  options: { key: string; value: string }[];
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
  enabled: boolean;
  indexerIds: string[];
  bypassIfHighestQuality: boolean;
  /** Every tag this rule applies to; empty means it has no tag (the default rule). */
  tagNames: string[];
  /** Place in the list of rules; the lowest number is the top rule. */
  position: number;
}

/** How several matching release rules are used: every one at once, or only the top match. */
export type ReleaseRuleMode = "combine" | "first-match";

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

/** Dedicated outbound notification provider types (#855), beyond the
 * generic webhook and the sniffed Discord embed. */
export type NotificationProvider =
  | "generic"
  | "discord"
  | "slack"
  | "telegram"
  | "pushover"
  | "ntfy"
  | "gotify"
  | "apprise";

/** Provider-specific connection fields that do not fit a single URL
 * (Telegram's bot token + chat id, Pushover's app token + user key, Gotify's
 * app token, ntfy's optional access token). */
export interface NotificationProviderConfig {
  botToken?: string | null;
  chatId?: string | null;
  appToken?: string | null;
  userKey?: string | null;
  accessToken?: string | null;
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
  provider: NotificationProvider;
  configJson: string | null;
  /** Comma-separated tag names (#990). Blank fires this connection for any title; a tagged connection fires only for a title sharing one of them. */
  tags: string;
  /** Radarr/Sonarr's Include Health Warnings (#1016): off, this connection skips the smaller advisory health warnings. */
  includeHealthWarnings?: boolean;
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

/** GET/PUT /api/notification-preferences: the outbound-dispatch gates. */
export interface NotificationPreferences {
  importCompletedEnabled: boolean;
  importFailedEnabled: boolean;
  providerHealthChangedEnabled: boolean;
  upgradeEnabled: boolean;
  renameEnabled: boolean;
  fileDeletedEnabled: boolean;
  manualInteractionRequiredEnabled: boolean;
  grabEnabled: boolean;
  addedEnabled: boolean;
  updateEnabled: boolean;
  stalledDownloadHandledEnabled: boolean;
  queueItemRemovedEnabled: boolean;
  orphanedDownloadRemovedEnabled: boolean;
  forcedImportEnabled: boolean;
  strikesPurgedEnabled: boolean;
  cleanupClearedEnabled: boolean;
  unmatchedImportGivenUpEnabled: boolean;
}

export interface DownloadClientItem {
  /** Saved on another computer and cannot be read here (#1094): listed as missing until entered again. */
  secretUnreadable?: boolean;
  secretProblem?: string | null;
  id: string;
  name: string;
  /** qbittorrent | sabnzbd | nzbget | transmission | deluge | utorrent */
  protocol: string;
  host?: string | null;
  port?: number | null;
  username?: string | null;
  endpointUrl: string | null;
  useSsl: boolean;
  /** Advanced: path prefix for a usenet client behind a reverse proxy, e.g. "sabnzbd" */
  urlBase?: string | null;
  /** Add every grab to this client paused/queued instead of starting it immediately */
  addPaused: boolean;
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
  /** Comma-separated tag names. Empty means this client is used for any title; otherwise it is used only for a title sharing one of these tags. */
  tags?: string;
  /** Remove a download from this client once Deluno has imported it successfully. Off by default. */
  removeCompleted?: boolean;
  /** Remove a download from this client once Deluno records it as failed. Off by default; distinct from the blocklist/replacement-search decision, which is unaffected. */
  removeFailed?: boolean;
  /** #811 (qBittorrent only): skip hash-checking a grab at add time. */
  skipHashCheck?: boolean;
  /** #811 (qBittorrent only): download pieces in order rather than rarest-first. */
  sequentialDownload?: boolean;
  /** #811 (qBittorrent only): prioritize the first and last pieces. */
  firstLastPiecePrio?: boolean;
  /** #811 (qBittorrent only): ratio at which this client's own share limits stop seeding a grab. Null uses the client's own default. */
  shareRatioLimit?: number | null;
  /** #811 (qBittorrent only): minutes to seed before the client's own share limits stop it. */
  shareSeedingTimeLimitMinutes?: number | null;
  /** #811 (qBittorrent only): minutes of no upload activity before the client's own share limits stop seeding. */
  shareInactiveSeedingTimeLimitMinutes?: number | null;
  /** #811 (qBittorrent only): save path sent at add time and reasserted with setLocation. Null uses the client's own default (or its category's save path). */
  location?: string | null;
  /** #999 (qBittorrent only): how a torrent's files land in the save folder. "default" leaves it to qBittorrent's own setting. */
  contentLayout?: "default" | "original" | "subfolder" | "no-subfolder";
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
  /** Finished torrents the client is still sharing (#1098); always 0 for Usenet. */
  seedingCount?: number;
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
  /** The client's own time-left reading in seconds; 0 when the client gives none. */
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
  /** #815: the torrent's own reported tracker privacy. Null when the client did not say (an older client, or Usenet, which has no tracker). */
  isPrivate?: boolean | null;
  /** #811/#970: how much of this download's data is available across its peers right now (0-1, can exceed 1.0 when more than one full copy is available). Null where the client does not report it. */
  availability?: number | null;
  /** #811/#970: when the client last saw any activity on this item. Null where the client does not report it. */
  lastActivityUtc?: string | null;
  /** #811: when the client finished this item. Null while still in progress, or where the client does not report it. */
  completionUtc?: string | null;
  /** #1026: this download's place in the client's own queue, 1 being next to start (Transmission and Deluge). Null where the client reports none. */
  queuePosition?: number | null;
  /** #1026: this download's own seed limits in plain words (Transmission and Deluge). Null where the client says nothing. */
  seedLimit?: string | null;
}

/**
 * What happened when the Queue's "why" dialog removed a download
 * (cleanup sheet J1/J2, #924). `entityId`/`mediaType` are the title this
 * download belonged to, when Deluno can still say — used by the caller to
 * unmonitor the title itself for the "I don't want it" reason, since that
 * happens on the movie/series side rather than here.
 */
export interface QueueRemovalResult {
  clientId: string;
  queueItemId: string;
  reason: string;
  succeeded: boolean;
  message: string;
  blocklisted: boolean;
  searchStarted: boolean;
  entityId: string | null;
  mediaType: string | null;
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
  /** The host of the client this mapping was created under (#809); a mapping applies to every client at that host, not only downloadClientId. */
  host?: string | null;
}

/** One entry in the seeding-rule matrix (#820): see SharingRuleItem.cs. */
export interface SharingRuleItem {
  id: string;
  priority: number;
  categoryPattern: string | null;
  trackerPattern: string | null;
  tagsCsv: string | null;
  tagMatchMode: "any" | "all" | string;
  mode: string | null;
  forHours: number | null;
  untilRatio: number | null;
  stuckAction: string | null;
  stuckAfterDays: number | null;
  completionAction: string | null;
  minSeeders: number | null;
  maxInactiveDays: number | null;
  isEnabled: boolean;
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
  /** #905: the configured rule (Settings -> Download health) driving this finding's strikes, when one matched. Null for a finding outside the rule engine. */
  ruleId: string | null;
  ruleActionPreset: string | null;
  ruleStrikeLimit: number | null;
  /** #905: when the next strike is due, given the rule's own timer (or the platform-wide fallback for a finding outside the rule engine). */
  nextStrikeUtc: string | null;
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

/** One configured download-health rule (#905). */
export interface DownloadHealthRule {
  id: string;
  kind: string;
  enabled: boolean;
  strikeLimit: number;
  timerMinutes: number;
  appliesTo: "torrents" | "usenet" | "both" | string;
  minCompletionPercent: number;
  maxCompletionPercent: number;
  resetStrikesOnProgress: boolean;
  actionPreset: "replace" | "retry-elsewhere" | "just-tell-me" | string;
  createdUtc: string;
  updatedUtc: string;
  threshold: number;
  /** #815: which tracker-privacy downloads this rule watches. */
  privacyScope: "public" | "private" | "both" | string;
  /** "Too slow" only (#951, G157): a hard ceiling on total minutes in queue, whatever the current speed. Null means no ceiling. */
  tooSlowMaxTotalMinutes: number | null;
  /** "Too slow" only (#951, G157): a download above this many bytes never trips this rule. Null/0 means no ceiling. */
  ignoreAboveSizeBytes: number | null;
  /** "Too slow" only (#951): never trips while the client's own alternate/scheduled speed limit is on. Only qBittorrent reports this. */
  skipWhileAlternateSpeedLimitOn: boolean;
  /** #951: minimum byte gain, at least, for a gain to reset this rule's strikes. Null/0 means any gain resets, as before this option existed. */
  resetStrikesMinimumProgressBytes: number | null;
  /** #1240: what the rule does to a private (or tagged) torrent at its count. "remove" is every rule's value until a person changes it. */
  privateAction: "remove" | "move-and-replace" | string;
  /** #1240: with a move, whether the release is also blocked for the title so the search does not pick it again. */
  blocklistWhenMoved: boolean;
}

/** One download client on the Keep seeding screen (#1240). */
export interface KeepSeedingClient {
  clientId: string;
  name: string;
  protocol: string;
  canKeepSeeding: boolean;
  reason: string | null;
  category: string | null;
}

/** One torrent site on the Keep seeding screen (#1240). */
export interface KeepSeedingIndexer {
  indexerId: string;
  name: string;
  /** What is known about the site: private, semi-private, public or unknown. */
  privacy: "private" | "semi-private" | "public" | "unknown" | string;
  /** The person's own answer; null follows what is known. */
  privateOverride: boolean | null;
  /** What Deluno treats the site as, after the override. */
  isPrivate: boolean;
  tags: string[];
  category: string | null;
}

export interface KeepSeedingTagCategory {
  tag: string;
  category: string;
}

/** A struck torrent Deluno meant to keep seeding and could not (#1240). */
export interface KeepSeedingProblem {
  key: string;
  clientId: string;
  clientName: string;
  itemId: string;
  releaseName: string;
  kind: "refused" | "needs-category" | string;
  message: string;
  category: string | null;
  firstSeenUtc: string;
  lastTriedUtc: string;
  attempts: number;
}

export interface KeepSeedingView {
  clients: KeepSeedingClient[];
  indexers: KeepSeedingIndexer[];
  tagCategories: KeepSeedingTagCategory[];
  problems: KeepSeedingProblem[];
}

/** What a client says about a category before it is used (#1240). */
export interface KeepSeedingCategoryCheck {
  clientId: string;
  clientName: string;
  category: string;
  status: "ready" | "missing" | "unsupported" | "unreachable" | "configuration" | string;
  message: string;
  supported: boolean;
  found: boolean;
}

/** One entry exempting a download from every health rule (#817). */
export interface DownloadHealthIgnoreEntry {
  id: string;
  hash: string | null;
  category: string | null;
  trackerDomain: string | null;
  /** #1194: a tag the client put on the torrent, or one Deluno holds for its title or indexer. */
  tag?: string | null;
  note: string | null;
  createdUtc: string;
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
  /** What the processor last said about this hand-off, when it can be asked (#511). */
  processor?: ProcessorHandoffObservation | null;
  /** What Deluno told the processor became of the file, and whether it heard (#580). */
  outcome?: ProcessorOutcomeRecord | null;
}

/** Mirrors `Deluno.Platform.Contracts.ProcessorOutcomeRecord`. */
export interface ProcessorOutcomeRecord {
  handoffId: string;
  processorName: string;
  libraryId: string;
  releaseName: string;
  /** `imported` or `not-imported`. */
  outcome: string;
  importedPath: string | null;
  reason: string | null;
  occurredUtc: string;
  /** `pending`, `delivered`, `refused`, `settled` or `given-up`. */
  delivery: string;
  attempts: number;
  lastAttemptUtc: string | null;
  nextAttemptUtc: string | null;
  released: boolean;
  processorSaid: string | null;
  recordedUtc: string;
}

/** Mirrors `Deluno.Platform.Contracts.ProcessorHandoffObservation`. */
export interface ProcessorHandoffObservation {
  state: string | null;
  message: string | null;
  queuePosition: number | null;
  scheduledForUtc: string | null;
  lastChangedUtc: string | null;
  checkedUtc: string;
  unreachableSinceUtc: string | null;
  answeringSinceUtc: string | null;
}

export interface ProcessorConnectionItem {
  id: string;
  name: string;
  provider: "fileflows-webhook" | "generic-webhook" | string;
  submissionUrl: string;
  authHeaderName: string;
  /** Optional address Deluno asks (GET) to prove the processor is up (#1055). */
  healthCheckUrl?: string | null;
  secretConfigured: boolean;
  /** Saved on another computer and cannot be read here (#1094): listed as missing until entered again. */
  secretUnreadable?: boolean;
  secretProblem?: string | null;
  isEnabled: boolean;
  healthStatus: "unknown" | "healthy" | "degraded" | "unreachable" | string;
  lastHealthMessage: string | null;
  lastHealthTestUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
}

/** A processor connection's optional path pair (#962): DelunoPath is the folder as Deluno sees it, ProcessorPath the same folder as the processor sees it. */
export interface ProcessorPathMappingItem {
  id: string;
  processorConnectionId: string;
  delunoPath: string;
  processorPath: string;
  isEnabled: boolean;
  priority: number;
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
  /** #810: this client's own queue can be reordered (move to top/up/down/bottom). False for a protocol whose queue API could not be confirmed (uTorrent). */
  supportsQueueReorder?: boolean;
  /** #810: qBittorrent only — bypass its own seed/ratio thresholds and force a transfer to start now. */
  supportsForceStart?: boolean;
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
  /** True when the client itself reports it is paused or on standby (SABnzbd's global pause, NZBGet's ServerStandBy/DownloadPaused/PostPaused/ScanPaused) — distinct from Deluno being unable to reach it. */
  clientPaused?: boolean | null;
  /** The client's own reported version, read alongside its queue/status (#812). Null when not yet read or the protocol has no version call. */
  clientVersion?: string | null;
  /** The client's own queue-level warnings (#813) — SABnzbd's have_warnings/mode=warnings. Null/empty when it has none, or the protocol has no such concept. */
  clientWarnings?: string[] | null;
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

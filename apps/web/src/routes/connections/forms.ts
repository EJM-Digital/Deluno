import type { DownloadClientItem, IndexerItem } from "../../lib/api";
import { CLIENT_PRESETS, INDEXER_PRESETS, type IndexerProtocol, type MediaScope } from "./presets";

/**
 * How strict this site is about sharing (#288).
 *
 * One answer, not five dials. The five columns behind it are a rule the user
 * has already made once, globally; all a source has to say is whether it is the
 * exception. "strict" is what a private tracker needs and what its own rules
 * describe: keep sharing a long time, give back at least what you took, and
 * never stop early on your own.
 */
export type SharingRule = "inherit" | "strict";

export const STRICT_SHARING = { forHours: 336, untilRatio: 1, stuckAfterDays: 14 } as const;

export function sharingRuleFrom(item: IndexerItem): SharingRule {
  return item.sharingMode || item.sharingForHours != null || item.sharingUntilRatio != null ||
    item.sharingStuckAction || item.sharingStuckAfterDays != null
    ? "strict"
    : "inherit";
}

export interface IndexerForm {
  name: string;
  protocol: IndexerProtocol;
  scope: MediaScope;
  baseUrl: string;
  apiKey: string;
  /** A non-default Newznab/Torznab API path, such as /api/v2 (#740). Blank uses /api. */
  apiPath: string;
  /** Extra Newznab/Torznab query parameters some indexers require (#741). */
  additionalParameters: string;
  /** This indexer's own anime category numbers (#742). Blank uses categories for anime too. */
  animeCategories: string;
  priority: string;
  requestIntervalSeconds: string;
  categories: string;
  isEnabled: boolean;
  sharingRule: SharingRule;
  minimumAgeMinutes: string;
  retentionDays: string;
  maximumSizeMb: string;
  /** The seeder floor a torrent from this indexer must clear to be grabbed (#738). Blank means unrestricted. */
  minimumSeeders: string;
  preferIndexerFlags: string;
  /** Comma-separated words a release's title must contain to be grabbed from this indexer (#744). */
  requiredWords: string;
  /** Comma-separated words that reject a release from this indexer before grab (#744). */
  ignoredWords: string;
  /** What this indexer's own MULTI tag means, e.g. "Original, French" (#746). Blank uses Deluno's fixed "Multilingual" default. */
  multiLanguages: string;
  availabilityDelayDays: string;
  rssEnabled: boolean;
  automaticSearchEnabled: boolean;
  interactiveSearchEnabled: boolean;
  /** The built-in tracker, for a "tracker" indexer (#606). */
  definitionId: string;
  /** Its settings that are not secret. */
  trackerSettings: Record<string, string>;
  /** Secrets typed in this session. Blank means keep the saved one: the screen never has it to show. */
  trackerSecrets: Record<string, string>;
}
export function emptyIndexerForm(): IndexerForm { return { name: "", protocol: "newznab", scope: "both", baseUrl: "", apiKey: "", apiPath: "", additionalParameters: "", animeCategories: "", priority: "10", requestIntervalSeconds: "", categories: INDEXER_PRESETS.find((preset) => preset.protocol === "newznab")!.defaultCategories("both"), isEnabled: true, sharingRule: "inherit", minimumAgeMinutes: "", retentionDays: "", maximumSizeMb: "", minimumSeeders: "", preferIndexerFlags: "", requiredWords: "", ignoredWords: "", multiLanguages: "", availabilityDelayDays: "", rssEnabled: true, automaticSearchEnabled: true, interactiveSearchEnabled: true, definitionId: "", trackerSettings: {}, trackerSecrets: {} }; }
export function indexerFormFrom(item: IndexerItem): IndexerForm { return { name: item.name, protocol: (["tracker", "torznab", "newznab", "rss", "custom"].includes(item.protocol) ? item.protocol : "custom") as IndexerProtocol, scope: item.mediaScope ?? "both", baseUrl: item.baseUrl, apiKey: "", apiPath: item.apiPath ?? "", additionalParameters: item.additionalParameters ?? "", animeCategories: item.animeCategories ?? "", priority: String(item.priority), requestIntervalSeconds: item.requestIntervalSeconds == null ? "" : String(item.requestIntervalSeconds), categories: item.categories, isEnabled: item.isEnabled, sharingRule: sharingRuleFrom(item), minimumAgeMinutes: item.minimumAgeMinutes == null ? "" : String(item.minimumAgeMinutes), retentionDays: item.retentionDays == null ? "" : String(item.retentionDays), maximumSizeMb: item.maximumSizeMb == null ? "" : String(item.maximumSizeMb), minimumSeeders: item.minimumSeeders == null ? "" : String(item.minimumSeeders), preferIndexerFlags: item.preferIndexerFlags ?? "", requiredWords: item.requiredWords ?? "", ignoredWords: item.ignoredWords ?? "", multiLanguages: item.multiLanguages ?? "", availabilityDelayDays: item.availabilityDelayDays == null ? "" : String(item.availabilityDelayDays), rssEnabled: item.rssEnabled !== false, automaticSearchEnabled: item.automaticSearchEnabled !== false, interactiveSearchEnabled: item.interactiveSearchEnabled !== false, definitionId: item.definitionId ?? "", trackerSettings: { ...(item.definitionSettings ?? {}) }, trackerSecrets: {} }; }
export function sameIndexer(a: IndexerForm, b: IndexerForm) { return (Object.keys(a) as (keyof IndexerForm)[]).every((key) => typeof a[key] === "object" ? sameRecord(a[key] as Record<string, string>, b[key] as Record<string, string>) : a[key] === b[key]); }
/** The same values, ignoring keys that are blank on one side and missing on the other. */
function sameRecord(a: Record<string, string>, b: Record<string, string>) { return [...new Set([...Object.keys(a), ...Object.keys(b)])].every((key) => (a[key] ?? "") === (b[key] ?? "")); }

export interface ClientForm { name: string; protocol: string; host: string; port: string; username: string; password: string; moviesCategory: string; tvCategory: string; priority: string; isEnabled: boolean; useSsl: boolean; urlBase: string; addPaused: boolean; tags: string; }
export function emptyClientForm(): ClientForm { const preset = CLIENT_PRESETS[0]!; return { name: preset.label, protocol: preset.protocol, host: "localhost", port: String(preset.defaultPort), username: "", password: "", moviesCategory: preset.defaultMoviesCategory, tvCategory: preset.defaultTvCategory, priority: "1", isEnabled: true, useSsl: false, urlBase: "", addPaused: false, tags: "" }; }
export function clientFormFrom(item: DownloadClientItem): ClientForm { return { name: item.name, protocol: item.protocol, host: item.host ?? "", port: item.port ? String(item.port) : "", username: item.username ?? "", password: "", moviesCategory: item.moviesCategory ?? item.categoryTemplate ?? "", tvCategory: item.tvCategory ?? "", priority: String(item.priority), isEnabled: item.isEnabled, useSsl: item.useSsl, urlBase: item.urlBase ?? "", addPaused: item.addPaused, tags: item.tags ?? "" }; }
export function sameClient(a: ClientForm, b: ClientForm) { return (Object.keys(a) as (keyof ClientForm)[]).every((key) => a[key] === b[key]); }

/**
 * What still has to be answered before this form can be saved.
 *
 * Lives here rather than inline in the screen so a create form's defaults can
 * be checked against the very rule that gates its Save (#293): a drawer that
 * opens complete must open valid, and one that does not must say what is
 * missing rather than sitting inert.
 */
export function clientFormErrors(form: ClientForm): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!CLIENT_PRESETS.some((item) => item.protocol === form.protocol)) {
    errors.protocol = "Choose the download client you actually run.";
  }
  if (!form.name.trim()) errors.name = "Give this client a name.";
  if (!form.host.trim()) errors.host = "Enter the host or IP.";
  if (!form.port.trim() || Number.isNaN(Number(form.port))) errors.port = "Enter a port number.";
  if (form.priority.trim() && (!/^\d+$/.test(form.priority.trim()) || Number(form.priority) < 1 || Number(form.priority) > 50)) {
    errors.priority = "Priority must be between 1 (highest) and 50 (lowest).";
  }
  return errors;
}

export function indexerFormErrors(form: IndexerForm, creating: boolean): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!form.name.trim()) errors.name = "Give this indexer a name.";
  if (!form.baseUrl.trim()) errors.baseUrl = "Enter the indexer URL.";
  if (form.protocol === "tracker" && !form.definitionId) errors.definitionId = "Choose which tracker this is.";
  const preset = INDEXER_PRESETS.find((item) => item.protocol === form.protocol);
  if (creating && preset?.requiresApiKey && !form.apiKey.trim()) errors.apiKey = "This indexer needs an API key.";
  for (const [key, label] of [["minimumAgeMinutes", "Minimum age"], ["retentionDays", "Retention"], ["maximumSizeMb", "Maximum size"], ["availabilityDelayDays", "Availability delay"], ["minimumSeeders", "Minimum seeders"]] as const) {
    const value = form[key];
    // One backslash (#607): `\\d` in a regex literal matches a literal backslash,
    // and every number typed here was refused.
    if (value.trim() && (!/^\d+$/.test(value.trim()) || Number(value) < 0)) errors[key] = `${label} must be a non-negative whole number.`;
  }
  if (form.preferIndexerFlags.length > 500) errors.preferIndexerFlags = "Preferred flags must be 500 characters or fewer.";
  if (form.requiredWords.length > 500) errors.requiredWords = "Required words must be 500 characters or fewer.";
  if (form.ignoredWords.length > 500) errors.ignoredWords = "Ignored words must be 500 characters or fewer.";
  if (form.multiLanguages.length > 200) errors.multiLanguages = "Multi-language meaning must be 200 characters or fewer.";
  return errors;
}

export type Section = "indexers" | "scoreboard" | "clients" | "routing";
export type DrawerState = { kind: "closed" } | { kind: "indexer"; id: string | null } | { kind: "client"; id: string | null } | { kind: "routing"; libraryId: string };
export function sameSet(a: string[], b: string[]) { return a.length === b.length && b.every((item) => new Set(a).has(item)); }

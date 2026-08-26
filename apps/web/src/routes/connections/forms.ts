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

export interface IndexerForm { name: string; protocol: IndexerProtocol; scope: MediaScope; baseUrl: string; apiKey: string; priority: string; requestIntervalSeconds: string; categories: string; isEnabled: boolean; sharingRule: SharingRule; }
export function emptyIndexerForm(): IndexerForm { return { name: "", protocol: "newznab", scope: "both", baseUrl: "", apiKey: "", priority: "10", requestIntervalSeconds: "", categories: INDEXER_PRESETS[1]!.defaultCategories("both"), isEnabled: true, sharingRule: "inherit" }; }
export function indexerFormFrom(item: IndexerItem): IndexerForm { return { name: item.name, protocol: (["torznab", "newznab", "rss", "custom"].includes(item.protocol) ? item.protocol : "custom") as IndexerProtocol, scope: item.mediaScope ?? "both", baseUrl: item.baseUrl, apiKey: "", priority: String(item.priority), requestIntervalSeconds: item.requestIntervalSeconds == null ? "" : String(item.requestIntervalSeconds), categories: item.categories, isEnabled: item.isEnabled, sharingRule: sharingRuleFrom(item) }; }
export function sameIndexer(a: IndexerForm, b: IndexerForm) { return (Object.keys(a) as (keyof IndexerForm)[]).every((key) => a[key] === b[key]); }

export interface ClientForm { name: string; protocol: string; host: string; port: string; username: string; password: string; moviesCategory: string; tvCategory: string; priority: string; isEnabled: boolean; }
export function emptyClientForm(): ClientForm { const preset = CLIENT_PRESETS[0]!; return { name: preset.label, protocol: preset.protocol, host: "localhost", port: String(preset.defaultPort), username: "", password: "", moviesCategory: preset.defaultMoviesCategory, tvCategory: preset.defaultTvCategory, priority: "1", isEnabled: true }; }
export function clientFormFrom(item: DownloadClientItem): ClientForm { return { name: item.name, protocol: item.protocol, host: item.host ?? "", port: item.port ? String(item.port) : "", username: item.username ?? "", password: "", moviesCategory: item.moviesCategory ?? item.categoryTemplate ?? "", tvCategory: item.tvCategory ?? "", priority: String(item.priority), isEnabled: item.isEnabled }; }
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
  return errors;
}

export function indexerFormErrors(form: IndexerForm, creating: boolean): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!form.name.trim()) errors.name = "Give this indexer a name.";
  if (!form.baseUrl.trim()) errors.baseUrl = "Enter the indexer URL.";
  const preset = INDEXER_PRESETS.find((item) => item.protocol === form.protocol);
  if (creating && preset?.requiresApiKey && !form.apiKey.trim()) errors.apiKey = "This indexer needs an API key.";
  return errors;
}

export type Section = "indexers" | "clients" | "routing";
export type DrawerState = { kind: "closed" } | { kind: "indexer"; id: string | null } | { kind: "client"; id: string | null } | { kind: "routing"; libraryId: string };
export function sameSet(a: string[], b: string[]) { return a.length === b.length && b.every((item) => new Set(a).has(item)); }

import type { DownloadClientItem, IndexerItem } from "../../lib/api";
import { CLIENT_PRESETS, INDEXER_PRESETS, type IndexerProtocol, type MediaScope } from "./presets";

export interface IndexerForm { name: string; protocol: IndexerProtocol; scope: MediaScope; baseUrl: string; apiKey: string; priority: string; requestIntervalSeconds: string; categories: string; isEnabled: boolean; }
export function emptyIndexerForm(): IndexerForm { return { name: "", protocol: "newznab", scope: "both", baseUrl: "", apiKey: "", priority: "10", requestIntervalSeconds: "", categories: INDEXER_PRESETS[1]!.defaultCategories("both"), isEnabled: true }; }
export function indexerFormFrom(item: IndexerItem): IndexerForm { return { name: item.name, protocol: (["torznab", "newznab", "rss", "custom"].includes(item.protocol) ? item.protocol : "custom") as IndexerProtocol, scope: item.mediaScope ?? "both", baseUrl: item.baseUrl, apiKey: "", priority: String(item.priority), requestIntervalSeconds: item.requestIntervalSeconds == null ? "" : String(item.requestIntervalSeconds), categories: item.categories, isEnabled: item.isEnabled }; }
export function sameIndexer(a: IndexerForm, b: IndexerForm) { return (Object.keys(a) as (keyof IndexerForm)[]).every((key) => a[key] === b[key]); }

export interface ClientForm { name: string; protocol: string; host: string; port: string; username: string; password: string; moviesCategory: string; tvCategory: string; priority: string; isEnabled: boolean; }
export function emptyClientForm(): ClientForm { const preset = CLIENT_PRESETS[0]!; return { name: "", protocol: preset.protocol, host: "localhost", port: String(preset.defaultPort), username: "", password: "", moviesCategory: preset.defaultMoviesCategory, tvCategory: preset.defaultTvCategory, priority: "1", isEnabled: true }; }
export function clientFormFrom(item: DownloadClientItem): ClientForm { return { name: item.name, protocol: item.protocol, host: item.host ?? "", port: item.port ? String(item.port) : "", username: item.username ?? "", password: "", moviesCategory: item.moviesCategory ?? item.categoryTemplate ?? "", tvCategory: item.tvCategory ?? "", priority: String(item.priority), isEnabled: item.isEnabled }; }
export function sameClient(a: ClientForm, b: ClientForm) { return (Object.keys(a) as (keyof ClientForm)[]).every((key) => a[key] === b[key]); }

export type Section = "indexers" | "clients" | "routing";
export type DrawerState = { kind: "closed" } | { kind: "indexer"; id: string | null } | { kind: "client"; id: string | null } | { kind: "routing"; libraryId: string };
export function sameSet(a: string[], b: string[]) { return a.length === b.length && b.every((item) => new Set(a).has(item)); }

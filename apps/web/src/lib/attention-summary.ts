import { fetchJson } from "./api";
import type { RouteSource } from "./route-cache";

/**
 * What needs a person, as the server works it out (#1185). `GET /api/attention/summary` is the one definition: the
 * bell's rows and its count, and the number the tray shows, come from the same function on the server. The browser
 * draws these rows and shows `count`; it adds nothing up and decides nothing about who is on the list.
 */
export interface AttentionSummaryRow {
  /** Unique among the rows: the row's key. */
  id: string;
  section: "media" | "deluno";
  /** What kind of problem; rows of one kind are what a pile of old ones groups. */
  kind: string;
  tone: "bad" | "warn";
  title: string;
  text: string;
  /** Where the title links. */
  to: string;
  /** The words on the row's main button. */
  action: string;
  /** How many problems the row stands for. */
  count: number;
  sinceUtc: string | null;
  /** The record behind the row (the stuck download, the import case, the job), for the real buttons. */
  data: unknown;
}

export interface AttentionSummary {
  /** The number the bell shows and the tray says: the sum of `rows`' counts. Notices are not in it. */
  count: number;
  rows: AttentionSummaryRow[];
  /** A newer Deluno or a newer connected app: listed on their own, never counted (an update ready is the blue dot). */
  notices: AttentionSummaryRow[];
  /** Leftover download folders and health suggestions: worth a look, never counted. */
  leftovers: number;
  suggestions: number;
}

export const emptyAttentionSummary: AttentionSummary = { count: 0, rows: [], notices: [], leftovers: 0, suggestions: 0 };

export const attentionSummarySource: RouteSource<AttentionSummary> = {
  key: ["src", "attention-summary"],
  label: "What needs a look",
  fetch: () => fetchJson<AttentionSummary>("/api/attention/summary")
};

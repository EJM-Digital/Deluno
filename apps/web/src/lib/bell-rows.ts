/**
 * What the bell lists (#1098): "needs you", in two sections, and a third for updates that are ready.
 *
 * The rows, their words and their count come from the server (`GET /api/attention/summary`, #1185); this file only
 * groups old ones into piles and picks each row's buttons from `alert-actions.ts`.
 *
 *  - Your media: stuck downloads (one per row, with the reason), failed imports, refused hand-offs, titles
 *    with no acceptable release.
 *  - Deluno: a connection down, a drive filling or failing, security, updates, the Machine checks.
 *
 * A pile of the same old problem is one row ("5 downloads have been stuck for over a day"); a new problem is
 * its own row, so something that has just gone wrong is never buried in a count. Each row carries its (at most
 * two) buttons from `alert-actions.ts`, so the bell and System's Health can never disagree about them.
 */
import type { AttentionSummary, AttentionSummaryRow } from "./attention-summary";
import type { DownloadQueueItem, NoReleaseRow, ProcessorHandoffItem } from "./api";
import type { RecoveryCaseRow } from "./needs-action";
import {
  connectionDownActions,
  connectionUpdateActions,
  failedImportActions,
  handoffActions,
  healthProblemActions,
  jobsGaveUpActions,
  noReleaseActions,
  recoveryActions,
  screenLinkActions,
  stuckDownloadActions,
  updateReadyActions,
  type AlertAction,
  type AlertActions
} from "./alert-actions";

export type BellSection = "media" | "deluno" | "updates";

export interface BellRow {
  key: string;
  section: BellSection;
  /** What kind of problem this is; rows of one kind are what a pile groups. */
  kind: string;
  tone: "bad" | "warn";
  title: string;
  /** What the buttons name in their accessible labels ("Retry Sintel (2010)"); unique among the rows. */
  subject?: string;
  text: string;
  /** Where the title links: Transfers for media, System's Health for Deluno. */
  to: string;
  actions: AlertActions;
  /** When it began, if known; a row without a time is new. */
  sinceUtc: string | null;
  /** How many problems this row stands for (more than one for a pile). */
  count: number;
}

export interface BellRows {
  media: BellRow[];
  deluno: BellRow[];
  /** A newer Deluno or connected app: listed on their own and never counted (an update ready is the blue dot). */
  updates: BellRow[];
}

/** A problem is old once it has stood this long. */
export const OLD_AFTER_MS = 24 * 60 * 60 * 1000;

/** Fewer old ones than this stay as their own rows. */
export const PILE_FROM = 3;

const HEALTH = "/system";

function plural(count: number, one: string, many = `${one}s`): string {
  return `${count.toLocaleString("en-AU")} ${count === 1 ? one : many}`;
}

function link(id: string, label: string, to: string, hint: string): AlertAction {
  return { id, label, hint, to };
}

function isOld(row: BellRow, now: number): boolean {
  if (!row.sinceUtc) return false;
  const since = Date.parse(row.sinceUtc);
  return Number.isFinite(since) && now - since >= OLD_AFTER_MS;
}

/** How a pile of one kind reads, and where its one button goes. */
interface PileWords {
  title: (count: number) => string;
  text: string;
  to: string;
  label: string;
  hint: string;
}

/**
 * Groups the old rows of each kind into one row when there are enough of them. The new ones, and a few old
 * ones, stay as their own rows, in the order they came.
 */
function pile(rows: BellRow[], now: number, words: (kind: string) => PileWords | null): BellRow[] {
  const oldByKind = new Map<string, BellRow[]>();
  for (const row of rows) {
    if (isOld(row, now)) oldByKind.set(row.kind, [...(oldByKind.get(row.kind) ?? []), row]);
  }

  const out: BellRow[] = [];
  const emitted = new Set<string>();
  for (const row of rows) {
    const olds = oldByKind.get(row.kind);
    const group = olds && olds.length >= PILE_FROM ? words(row.kind) : null;
    if (olds && olds.includes(row) && group) {
      if (emitted.has(row.kind)) continue;
      emitted.add(row.kind);
      const oldest = olds.map((item) => item.sinceUtc).filter((value): value is string => Boolean(value)).sort()[0] ?? null;
      out.push({
        key: `pile:${row.kind}`,
        section: row.section,
        kind: row.kind,
        tone: olds.some((item) => item.tone === "bad") ? "bad" : "warn",
        title: group.title(olds.length),
        text: group.text,
        to: group.to,
        actions: [link(`pile-open:${row.kind}`, group.label, group.to, group.hint)],
        sinceUtc: oldest,
        count: olds.length
      });
      continue;
    }
    out.push(row);
  }
  return out;
}

const MEDIA_PILES: Record<string, PileWords> = {
  stuck: {
    title: (count) => `${plural(count, "download has", "downloads have")} been stuck for over a day`,
    text: "Go through them in Transfers.",
    to: "/queue",
    label: "Open Transfers",
    hint: "Opens Transfers, where each one can be replaced or dismissed. Nothing is changed."
  },
  import: {
    title: (count) => `${plural(count, "import")} didn’t finish`,
    text: "They have been waiting for over a day. Go through them in Transfers.",
    to: "/queue",
    label: "Open Transfers",
    hint: "Opens Transfers, where each one can be retried or dismissed. Nothing is changed."
  },
  handoff: {
    title: (count) => `${plural(count, "hand-off")} to the processor failed`,
    text: "They have been waiting for over a day. Go through them in Transfers.",
    to: "/queue",
    label: "Open Transfers",
    hint: "Opens Transfers, where each one can be retried or dismissed. Nothing is changed."
  },
  recovery: {
    title: (count) => `${plural(count, "import needs", "imports need")} a look`,
    text: "They have been waiting for over a day. Go through them in Transfers.",
    to: "/queue",
    label: "Open Transfers",
    hint: "Opens Transfers, where each one can be retried or dismissed. Nothing is changed."
  },
  "no-release": {
    title: (count) => `${plural(count, "title has", "titles have")} no acceptable release`,
    text: "Nothing Deluno would take has turned up for over a day.",
    to: "/search-cycles/missing",
    label: "Open Wanted",
    hint: "Opens the wanted list, where each one can be searched again or dismissed. Nothing is changed."
  }
};

const DELUNO_PILE_LABELS: Record<string, string> = {
  indexer: "indexer",
  "download-client": "download client",
  library: "library",
  processor: "processor",
  "media-server": "media server",
  metadata: "metadata",
  seerr: "request server",
  machine: "computer",
  system: "system",
  proxy: "proxy",
  tags: "tag",
  grab: "grab"
};

function delunoPile(kind: string): PileWords | null {
  const category = kind.startsWith("health:") ? kind.slice("health:".length) : "";
  if (!category) return null;
  const noun = DELUNO_PILE_LABELS[category] ?? "health";
  return {
    title: (count) => `${plural(count, `${noun} problem`)} for over a day`,
    text: "Go through them in System’s Health.",
    to: HEALTH,
    label: "Open System",
    hint: "Opens System’s Health, where each one is explained and can be snoozed. Nothing is changed."
  };
}

/** The buttons a server row offers, by what kind of row it is. The record behind the row is what they act on. */
function actionsFor(row: AttentionSummaryRow): AlertActions {
  const data = row.data as Record<string, unknown> | null;
  switch (row.kind) {
    case "stuck":
      return stuckDownloadActions(data as unknown as DownloadQueueItem);
    case "recovery":
      return recoveryActions(data as unknown as RecoveryCaseRow);
    case "import":
      return failedImportActions({ id: String(data?.id) });
    case "handoff":
      return handoffActions(data as unknown as Pick<ProcessorHandoffItem, "id" | "releaseName">);
    case "no-release":
      return noReleaseActions(data as unknown as NoReleaseRow);
    case "connection": {
      const id = String(data?.id);
      const name = String(data?.name);
      if (row.id.startsWith("indexer:")) return connectionDownActions("indexer", id, name);
      if (row.id.startsWith("client:")) return connectionDownActions("downloadClient", id, name);
      return healthProblemActions({ id: `processor:${id}`, category: "processor", link: "/settings/processing" });
    }
    case "jobs":
      return jobsGaveUpActions(row.id === "failed-title-jobs" ? "title" : "background");
    case "update":
      return updateReadyActions();
    case "connection-update":
      return connectionUpdateActions(String(data?.kind), String(data?.id));
    default:
      if (row.kind.startsWith("health:")) return healthProblemActions(data as unknown as Parameters<typeof healthProblemActions>[0]);
      return screenLinkActions(row.action, row.to, `open:${row.id}`);
  }
}

function toBellRow(row: AttentionSummaryRow, importNumber: number): BellRow {
  return {
    key: row.id,
    subject: row.kind === "import" ? `failed import ${importNumber}` : undefined,
    section: row.section,
    kind: row.kind,
    tone: row.tone,
    title: row.title,
    text: row.text,
    to: row.to,
    actions: actionsFor(row),
    sinceUtc: row.sinceUtc,
    count: row.count
  };
}

/**
 * The bell's rows from the server's summary (#1185). Nothing is added up here: `summary.count` is the number, and
 * every row is one the server put on the list. A pile of old ones of the same kind is one row, as before.
 */
export function bellRows(summary: AttentionSummary, now: number = Date.now()): BellRows {
  let imports = 0;
  const rows = summary.rows.map((row) => toBellRow(row, row.kind === "import" ? ++imports : 0));
  const media = rows.filter((row) => row.section === "media");
  const deluno = rows.filter((row) => row.section === "deluno");

  // A button name must be unique on screen: two rows about the same title are told apart by a number.
  const unique = (list: BellRow[]) => {
    const seen = new Map<string, number>();
    return list.map((row) => {
      const base = row.subject ?? row.title;
      const n = (seen.get(base) ?? 0) + 1;
      seen.set(base, n);
      return n === 1 ? row : { ...row, subject: `${base} (${n})` };
    });
  };
  const worstFirst = (list: BellRow[]) => list.map((row, index) => ({ row, index })).sort((a, b) => Number(b.row.tone === "bad") - Number(a.row.tone === "bad") || a.index - b.index).map((entry) => entry.row);
  return {
    media: unique(worstFirst(pile(media, now, (kind) => MEDIA_PILES[kind] ?? null))),
    deluno: unique(worstFirst(pile(deluno, now, delunoPile))),
    updates: summary.notices.map((row) => ({ ...toBellRow(row, 0), section: "updates" as const }))
  };
}

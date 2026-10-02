/**
 * What the bell lists (#1098): "needs you", in two sections.
 *
 *  - Your media: stuck downloads (one per row, with the reason), failed imports, refused hand-offs, titles
 *    with no acceptable release.
 *  - Deluno: a connection down, a drive filling or failing, security, updates, the Machine checks.
 *
 * A pile of the same old problem is one row ("5 downloads have been stuck for over a day"); a new problem is
 * its own row, so something that has just gone wrong is never buried in a count. Each row carries its (at most
 * two) buttons from `alert-actions.ts`, so the bell and System's Health can never disagree about them.
 */
import type { DashboardAttention } from "../routes/dashboard/attention";
import {
  connectionDownActions,
  failedImportActions,
  handoffActions,
  healthProblemActions,
  jobsGaveUpActions,
  noReleaseActions,
  recoveryActions,
  screenLinkActions,
  stuckDownloadActions,
  stuckDownloadReason,
  updateReadyActions,
  type AlertAction,
  type AlertActions
} from "./alert-actions";

export type BellSection = "media" | "deluno";

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

function ageOf(item: { lastActivityUtc?: string | null; addedUtc?: string | null }): string | null {
  return item.lastActivityUtc ?? item.addedUtc ?? null;
}

export function bellRows(attention: DashboardAttention, now: number = Date.now()): BellRows {
  const { downloads } = attention;
  const media: BellRow[] = [];
  const deluno: BellRow[] = [];

  // ------------------------------------------------------------ your media
  for (const item of downloads.queueAttention) {
    media.push({
      key: `stuck:${item.clientId}:${item.id}`,
      section: "media",
      kind: "stuck",
      tone: item.status === "stalled" || item.errorMessage ? "bad" : "warn",
      title: item.title || item.releaseName,
      text: stuckDownloadReason(item),
      to: "/queue",
      actions: stuckDownloadActions(item),
      sinceUtc: ageOf(item),
      count: 1
    });
  }
  for (const entry of downloads.recoveryCases) {
    media.push({
      key: `recovery:${entry.mediaType}:${entry.id}`,
      section: "media",
      kind: "recovery",
      tone: "warn",
      title: entry.title,
      text: `${entry.mediaType === "movie" ? "Movies" : "TV"}: ${entry.summary}`,
      to: "/queue",
      actions: recoveryActions(entry),
      sinceUtc: entry.detectedUtc ?? null,
      count: 1
    });
  }
  for (const [index, job] of downloads.failedImportJobs.entries()) {
    media.push({
      key: `import:${job.id}`,
      subject: `failed import ${index + 1}`,
      section: "media",
      kind: "import",
      tone: "bad",
      title: "An import didn’t finish",
      text: job.lastError ?? "It stopped without saying why.",
      to: "/queue",
      actions: failedImportActions(job),
      sinceUtc: job.completedUtc ?? job.createdUtc ?? null,
      count: 1
    });
  }
  for (const handoff of downloads.handoffs) {
    media.push({
      key: `handoff:${handoff.id}`,
      section: "media",
      kind: "handoff",
      tone: "bad",
      title: handoff.releaseName,
      text: handoff.failureMessage ?? `${handoff.processorName ?? "The processor"} could not finish it.`,
      to: "/queue",
      actions: handoffActions(handoff),
      sinceUtc: handoff.updatedUtc ?? handoff.createdUtc ?? null,
      count: 1
    });
  }
  for (const row of attention.noRelease) {
    media.push({
      key: `no-release:${row.mediaType}:${row.id}`,
      section: "media",
      kind: "no-release",
      tone: "warn",
      title: row.year ? `${row.title} (${row.year})` : row.title,
      text: "No acceptable release yet: every search found nothing Deluno would take.",
      to: row.mediaType === "movie" ? `/movies/${encodeURIComponent(row.id)}` : `/tv/${encodeURIComponent(row.id)}`,
      actions: noReleaseActions(row),
      sinceUtc: row.missingSinceUtc ?? row.lastSearchUtc,
      count: 1
    });
  }

  // ---------------------------------------------------------------- Deluno
  for (const indexer of downloads.indexers) {
    deluno.push({
      key: `indexer:${indexer.id}`,
      section: "deluno",
      kind: "connection",
      tone: "warn",
      title: `${indexer.name} isn’t answering`,
      text: indexer.lastHealthMessage || "Deluno can’t search this site until it answers.",
      to: HEALTH,
      actions: connectionDownActions("indexer", indexer.id, indexer.name),
      sinceUtc: null,
      count: 1
    });
  }
  for (const client of downloads.clients) {
    deluno.push({
      key: `client:${client.id}`,
      section: "deluno",
      kind: "connection",
      tone: "warn",
      title: !client.reachable ? `${client.name} isn’t answering` : client.paused ? `${client.name} is paused` : `${client.name} has warnings`,
      text: !client.reachable ? "Downloads can’t start there until it answers." : client.paused ? "Nothing downloads there while it is paused." : "The download client reported something to clear.",
      to: HEALTH,
      actions: connectionDownActions("downloadClient", client.id, client.name),
      sinceUtc: null,
      count: 1
    });
  }
  for (const processor of downloads.processors) {
    deluno.push({
      key: `processor:${processor.id}`,
      section: "deluno",
      kind: "connection",
      tone: "warn",
      title: `${processor.name} isn’t answering`,
      text: processor.lastHealthMessage || "Finished downloads can’t be cleaned until it answers.",
      to: HEALTH,
      actions: healthProblemActions({ id: `processor:${processor.id}`, category: "processor", link: "/settings/processing" }),
      sinceUtc: null,
      count: 1
    });
  }
  for (const problem of attention.healthListed) {
    deluno.push({
      key: `health:${problem.id}`,
      section: "deluno",
      kind: `health:${problem.category}`,
      tone: "warn",
      title: problem.title,
      text: problem.message,
      to: HEALTH,
      actions: healthProblemActions(problem),
      sinceUtc: problem.sinceUtc,
      count: 1
    });
  }
  // The rest of System's own rows: outage, the queue gate, collections, request servers, jobs that gave up,
  // workers, setup, "more problems", and an update that is ready.
  const handled = new Set(["clients", "indexers", "processors", "stalled", "queue-warnings", "recovery", "failed-imports", "handoffs", "no-release"]);
  for (const item of attention.items) {
    if (item.tab !== "system" || handled.has(item.id) || item.id.startsWith("health:") && item.id !== "health:more") continue;
    if (item.id === "update") {
      deluno.push({ key: "update", section: "deluno", kind: "update", tone: "warn", title: item.title, text: item.text, to: "/system/updates", actions: updateReadyActions(), sinceUtc: null, count: 1 });
      continue;
    }
    if (item.id === "failed-title-jobs") {
      deluno.push({ key: "failed-title-jobs", section: "deluno", kind: "jobs", tone: "bad", title: item.title, text: item.text, to: item.to, actions: jobsGaveUpActions("title"), sinceUtc: null, count: item.count });
      continue;
    }
    if (item.id === "failed-jobs") {
      deluno.push({
        key: "failed-jobs",
        section: "deluno",
        kind: "jobs",
        tone: "bad",
        title: item.title,
        text: item.text,
        to: item.to,
        actions: jobsGaveUpActions("background"),
        sinceUtc: null,
        count: item.count
      });
      continue;
    }
    deluno.push({
      key: item.id,
      section: "deluno",
      kind: item.id,
      tone: item.tone,
      title: item.title,
      text: item.text,
      to: item.to.startsWith("/?tab=") ? HEALTH : item.to,
      actions: screenLinkActions(item.action, item.to.startsWith("/?tab=") ? HEALTH : item.to, `open:${item.id}`),
      sinceUtc: null,
      count: item.count
    });
  }

  // A button name must be unique on screen: two rows about the same title are told apart by a number.
  const unique = (rows: BellRow[]) => {
    const seen = new Map<string, number>();
    return rows.map((row) => {
      const base = row.subject ?? row.title;
      const n = (seen.get(base) ?? 0) + 1;
      seen.set(base, n);
      return n === 1 ? row : { ...row, subject: `${base} (${n})` };
    });
  };
  const worstFirst = (rows: BellRow[]) => rows.map((row, index) => ({ row, index })).sort((a, b) => Number(b.row.tone === "bad") - Number(a.row.tone === "bad") || a.index - b.index).map((entry) => entry.row);
  return {
    media: unique(worstFirst(pile(media, now, (kind) => MEDIA_PILES[kind] ?? null))),
    deluno: unique(worstFirst(pile(deluno, now, delunoPile)))
  };
}

/**
 * The one place a state gets a colour, and the meaning of each colour.
 *
 * #290 settled what the hues mean when it took hue away from navigation.
 * Nothing written since had been checked against it, because nothing in the
 * codebase stated it: every screen picked its own tone at the point of use, so
 * the same state could be — and was — three different colours at once. A
 * release ready to import was green in Transfers, grey in the pipeline strip
 * and blue on a library card. See `AUDIT-002-status-colours.md`.
 *
 * A state cannot be coloured twice if only one place colours it. That is the
 * whole mechanism; `status-tones.test.ts` is what keeps it true.
 */

/**
 * Five tones, one set of names.
 *
 * `Chip` took `ok warn info muted bad` and `StatusLed` took
 * `ok warn info idle danger` — the same five ideas under two vocabularies, so
 * nothing could assert that a state was coloured the same way in both. `muted`
 * and `danger` are gone; `idle` and `bad` are the words.
 */
export type Tone = "ok" | "warn" | "info" | "bad" | "idle";

/**
 * What each tone promises. Amber is the load-bearing one: it is the only signal
 * that means *you* have to do something, and spending it on work that is
 * proceeding normally is what teaches people to stop reading it.
 */
export const TONE_MEANING: Record<Tone, string> = {
  ok: "Healthy. Done. A colour whose absence you scan for.",
  warn: "Needs you. Nothing happens until you act.",
  info: "Work in motion — queued, running, transferring, processing.",
  bad: "Failed.",
  idle: "Nothing to do. Off, unknown, or not applicable."
};

export interface StatusPresentation {
  tone: Tone;
  label: string;
}

/**
 * Every state Deluno shows a colour for, with the one tone and the one label it
 * gets wherever it appears.
 *
 * Grouped by what the state belongs to, not by which screen shows it — a state
 * that appears on three screens has one entry, which is the point.
 */
export const STATUS_PRESENTATION = {
  // ── Jobs ────────────────────────────────────────────────────────────────
  // Queued was grey and Running was amber. #290 names both blue: a job in
  // flight is motion, and a queued one is motion that has not started. Neither
  // needs a person, which is what amber had been claiming.
  "job.queued": { tone: "info", label: "Queued" },
  "job.running": { tone: "info", label: "Running" },
  "job.completed": { tone: "ok", label: "Completed" },
  "job.failed": { tone: "bad", label: "Failed" },
  // Failed *and* out of retries. This one genuinely needs a person, which is
  // what separates it from a failure that will be tried again.
  "job.deadLetter": { tone: "warn", label: "Gave up" },

  // ── Transfers and the acquisition pipeline ──────────────────────────────
  "transfer.queued": { tone: "info", label: "Queued" },
  "transfer.downloading": { tone: "info", label: "Downloading" },
  // Stopped, and it will not restart itself.
  "transfer.stalled": { tone: "warn", label: "Stalled" },
  // Mid-pipeline, not finished — it was green in Transfers, which is the
  // colour for done, and grey in the pipeline strip, which is the colour for
  // nothing happening.
  "transfer.importReady": { tone: "info", label: "Ready to import" },
  "transfer.importQueued": { tone: "info", label: "Import queued" },
  "transfer.importing": { tone: "info", label: "Importing" },
  "transfer.imported": { tone: "ok", label: "Imported" },
  "transfer.importFailed": { tone: "bad", label: "Import failed" },
  // In flight, and blue in two of the three places that showed it.
  "transfer.waitingForProcessor": { tone: "info", label: "Waiting for processor" },
  "transfer.processing": { tone: "info", label: "Processing" },
  "transfer.processed": { tone: "ok", label: "Processed" },
  "transfer.processingFailed": { tone: "bad", label: "Processing failed" },
  // Finished and seeding: an obligation to the site it came from, not work.
  "transfer.sharing": { tone: "info", label: "Sharing" },
  "transfer.needsALook": { tone: "warn", label: "Needs a look" },

  // ── Connections ─────────────────────────────────────────────────────────
  "connection.healthy": { tone: "ok", label: "Healthy" },
  "connection.degraded": { tone: "warn", label: "Degraded" },
  "connection.unhealthy": { tone: "bad", label: "Unhealthy" },
  "connection.untested": { tone: "idle", label: "Untested" },
  "connection.off": { tone: "idle", label: "Off" },
  // Deluno backed off deliberately and resumes on its own. Nothing to do.
  "connection.rateLimited": { tone: "info", label: "Rate-limited" },
  // A configuration fact, not motion.
  "connection.categoryRoute": { tone: "idle", label: "Routed" }
} as const satisfies Record<string, StatusPresentation>;

export type StatusKey = keyof typeof STATUS_PRESENTATION;

export function statusPresentation(key: StatusKey): StatusPresentation {
  return STATUS_PRESENTATION[key];
}

export function statusTone(key: StatusKey): Tone {
  return STATUS_PRESENTATION[key].tone;
}

export function statusLabel(key: StatusKey): string {
  return STATUS_PRESENTATION[key].label;
}

/* ═══════════════ THE MARK ON A TITLE ═══════════════ */

/**
 * The five rungs a title climbs, from `DESIGN-001-title-marks.md`.
 *
 * A separate ladder from the operational tones above, and deliberately so.
 * Nothing on a poster is a failure or a machine's health — those live in
 * Transfers, Activity and Needs You — which is what frees red for *Missing* and
 * leaves amber meaning what it means everywhere else. Read the operational
 * table for a chip about the machinery; read this one for a mark on a title.
 *
 * Missing → Downloading → Upgradable → Quality met is the order a title climbs.
 * Nobody has to be taught that gold is above green.
 */
export type TitleMark = "missing" | "downloading" | "upgrade" | "covered" | "upcoming";

export interface TitleMarkPresentation {
  /** The dot's fill, as a Tailwind background class. */
  dot: string;
  label: string;
  hint: string;
  /**
   * Whether a half-grey dot is meaningful for this rung.
   *
   * The half means the monitoring toggle and nothing else, so it appears only
   * where monitoring is deciding something *now*. A transfer under way finishes
   * regardless, and a title that already has what you asked for has left the
   * lifecycle — if its file later disappears it drops back to Missing and the
   * question comes back with it.
   */
  canBeHalf: boolean;
}

export const TITLE_MARK_PRESENTATION: Record<TitleMark, TitleMarkPresentation> = {
  missing: {
    dot: "bg-destructive",
    label: "Missing",
    hint: "It is out and Deluno does not have it yet.",
    canBeHalf: true
  },
  downloading: {
    dot: "bg-info",
    label: "Downloading",
    hint: "Coming down, processing, or importing.",
    canBeHalf: false
  },
  upgrade: {
    dot: "bg-success",
    label: "Upgradable",
    hint: "Here and watchable tonight, with room to get better.",
    canBeHalf: true
  },
  covered: {
    dot: "bg-mark-quality-met",
    label: "Quality met",
    hint: "The quality your profile asked for. Deluno has stopped looking.",
    canBeHalf: false
  },
  upcoming: {
    dot: "bg-mark-upcoming",
    label: "Upcoming",
    hint: "Not out yet, or the episode has not aired.",
    canBeHalf: true
  }
};

/** The order a title climbs. Lower index is a lower rung. */
export const TITLE_MARK_LADDER: readonly TitleMark[] = [
  "missing",
  "downloading",
  "upgrade",
  "covered"
] as const;

/**
 * The rung a show sits on: the lowest any *aired* episode is on, so it never
 * overstates how well a show is doing.
 */
export function lowestMark(marks: readonly TitleMark[]): TitleMark | null {
  let lowest: TitleMark | null = null;
  for (const mark of marks) {
    const rank = TITLE_MARK_LADDER.indexOf(mark);
    if (rank < 0) continue;
    if (lowest === null || rank < TITLE_MARK_LADDER.indexOf(lowest)) lowest = mark;
  }
  return lowest;
}

/**
 * The rung a title is on, from what Deluno actually knows about it.
 *
 * Deliberately not derived from `hasFile` alone, which is all the availability
 * chip ever had and is why an imported film could read *Downloading* (#299) and
 * a below-target one read the same as a finished one. The stored wanted status
 * says which of the four the title is on; live transfer state, when there is
 * any, outranks all of them because a transfer is happening now.
 *
 * A show takes the lowest rung any *aired* episode is on, so it never overstates
 * how well it is doing — thirteen of eighteen held is Missing, not Upgradable.
 */
export function titleMark(item: {
  wantedStatus?: string | null;
  /** Live, from download telemetry. Never inferred from a wanted status. */
  isTransferring?: boolean;
  airedEpisodeCount?: number;
  airedWithFileCount?: number;
  airedUpgradableCount?: number;
  nextAirDateUtc?: string | null;
}): TitleMark {
  if (item.isTransferring) return "downloading";

  // A show is judged on its episodes, which know more than its title-level row.
  const aired = item.airedEpisodeCount;
  if (typeof aired === "number") {
    if (aired === 0) {
      return item.nextAirDateUtc ? "upcoming" : "missing";
    }
    const held = item.airedWithFileCount ?? 0;
    if (held < aired) return "missing";
    if ((item.airedUpgradableCount ?? 0) > 0) return "upgrade";
    return "covered";
  }

  switch (item.wantedStatus) {
    case "covered":
      return "covered";
    case "upgrade":
      return "upgrade";
    case "upcoming":
      return "upcoming";
    default:
      return "missing";
  }
}

/**
 * How much of what you asked for beyond the title is here — episodes on a show.
 *
 * Counts what has **aired**, never what will exist, or every ongoing show reads
 * permanently unfinished, which is true of all of them and so says nothing.
 * Returns null when nothing was asked for, and the bar then claims nothing.
 */
export function titleBarFraction(item: {
  airedEpisodeCount?: number;
  airedWithFileCount?: number;
}): number | null {
  const aired = item.airedEpisodeCount;
  if (typeof aired !== "number" || aired <= 0) return null;
  return Math.min(1, Math.max(0, (item.airedWithFileCount ?? 0) / aired));
}

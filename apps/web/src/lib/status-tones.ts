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
export type TitleMark = "missing" | "downloading" | "upgrade" | "covered" | "airing" | "upcoming";

export interface TitleMarkPresentation {
  /**
   * Every class spelled out, never derived.
   *
   * These were built at the point of use by string surgery —
   * `presentation.dot.replace("bg-", "text-")` — which produces a class name
   * that appears nowhere in the source. Tailwind generates only the literals it
   * can see, so `text-mark-quality-met` and `text-mark-upcoming` were **purged
   * from the stylesheet** and the counts that asked for them rendered with no
   * colour at all, while `text-destructive` and `text-success` survived because
   * other files happened to spell them out. Half a legend, silently.
   *
   * A derived class cannot be checked by anything: not the compiler, not the
   * bundler, not a test that reads the source. Literals can.
   */
  /** The dot's fill. */
  dot: string;
  /** The same colour as text, for a count or a label. */
  text: string;
  /** A faint wash of it, for a chip that is carrying the colour rather than wearing it. */
  tint: string;
  /** The raw custom property, for an inline `hsl(var(...))` — an SVG arc, say. */
  cssVar: string;
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
  /**
   * An extra class for a rung that is drawn as something more than a colour.
   *
   * Only Quality met has one. The other four rungs all mean "Deluno is still
   * working on this"; Quality met is the only one that means it is done, and it
   * had been drawn as just another colour on the ladder. See `.mark-grail` in
   * `index.css` for what it does and why the sheen is off-frame most of the
   * time.
   *
   * Spelled out, like every other class in this table, because one built at the
   * point of use is invisible to Tailwind and gets purged — which is exactly
   * what happened to `text-mark-quality-met` and `text-mark-upcoming`.
   */
  sheen?: string;
}

export const TITLE_MARK_PRESENTATION: Record<TitleMark, TitleMarkPresentation> = {
  missing: {
    dot: "bg-destructive",
    text: "text-destructive",
    tint: "bg-destructive/15",
    cssVar: "--destructive",
    label: "Missing",
    hint: "It is out and Deluno does not have it yet. Deluno searches for this on its schedule.",
    canBeHalf: true
  },
  downloading: {
    dot: "bg-info",
    text: "text-info",
    tint: "bg-info/15",
    cssVar: "--info",
    label: "Downloading",
    hint: "Coming down, processing, or importing.",
    canBeHalf: false
  },
  upgrade: {
    dot: "bg-success",
    text: "text-success",
    tint: "bg-success/15",
    cssVar: "--success",
    label: "Upgradable",
    hint: "You have this and can watch it tonight. Deluno is still looking for a better copy.",
    canBeHalf: true
  },
  covered: {
    dot: "bg-mark-quality-met",
    text: "text-mark-quality-met",
    tint: "bg-mark-quality-met/15",
    cssVar: "--mark-quality-met",
    label: "Quality met",
    hint: "This is the quality your Library Profile asked for, so Deluno has stopped looking.",
    canBeHalf: false,
    sheen: "mark-grail"
  },
  airing: {
    dot: "bg-mark-airing",
    text: "text-mark-airing",
    tint: "bg-mark-airing/15",
    cssVar: "--mark-airing",
    label: "Up to date",
    hint: "You have every episode that has aired. More are still to come, and Deluno will look for them as they do.",
    canBeHalf: true
  },
  upcoming: {
    dot: "bg-mark-upcoming",
    text: "text-mark-upcoming",
    tint: "bg-mark-upcoming/15",
    cssVar: "--mark-upcoming",
    label: "Upcoming",
    hint: "Not out yet, or the episode has not aired. Deluno will start looking on release.",
    canBeHalf: true
  }
};

/**
 * What a title whose stored state this build does not recognise gets.
 *
 * Reached only by a value written by a newer build. It deliberately claims no
 * rung: `titleMark` coerces an unrecognised value to *Missing*, and Missing
 * means "go and download this" — the wrong thing to tell a reader about a state
 * nobody here understands. "Tracked" is the most the mere existence of a row
 * can support.
 */
export const UNRECOGNISED_TITLE_MARK: TitleMarkPresentation = {
  // Neutral, and deliberately not a rung's colour. It borrowed Upcoming's,
  // which was a muted slate at the time and read as "nothing to do" — then
  // Upcoming became violet and this would have inherited a hue that claims a
  // place on the ladder for a state nobody here understands.
  dot: "bg-muted-foreground",
  text: "text-muted-foreground",
  tint: "bg-muted-foreground/15",
  cssVar: "--muted-foreground",
  label: "Tracked",
  hint: "Deluno holds this title but does not recognise its current state.",
  canBeHalf: false
};

/**
 * The mark a stored wanted status names, for a title judged on its own row — an
 * episode, or a movie, which has no episodes to be judged on instead.
 *
 * This used to be a second table, `WANTED_STATUS_PRESENTATION`, carrying its own
 * tones: Missing was blue there and red on the poster, Quality met green there
 * and gold on the poster. Same four states, two colourings — the whole of #302
 * in one file. There is one table now, and this reads it.
 */
export function wantedStatusPresentation(value: string | null | undefined): TitleMarkPresentation {
  const known: readonly string[] = ["missing", "upgrade", "covered", "upcoming"];
  if (!value || !known.includes(value)) return UNRECOGNISED_TITLE_MARK;
  return TITLE_MARK_PRESENTATION[value as TitleMark];
}

/** The order a title climbs. Lower index is a lower rung. */
export const TITLE_MARK_LADDER: readonly TitleMark[] = [
  "missing",
  "downloading",
  "upgrade",
  // Above Upgradable because everything you hold is at the quality asked for,
  // and below Quality met because the show is not finished — there is more
  // coming and Deluno has not stopped looking.
  "airing",
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
 * chip ever had and is why an imported movie could read *Downloading* (#299) and
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

  // **The browser no longer decides a show's rung.**
  //
  // It used to, from these same episode counts, because the server's stored
  // status was decided from the title-level row — one arbitrary file — and was
  // wrong for a collection. Two answers, and they disagreed: on the rig,
  // Severance with three of twenty episodes was "Quality met" to the chips and
  // "Missing" on its own poster, and clicking the chip returned a title whose
  // poster contradicted it.
  //
  // The rule now lives once on the server (`SeriesRung`), decided from the
  // episodes and served — so the shelf, the chips and the filter cannot drift.
  // What the episode counts still do here is fill the ring; see `titleProgress`.
  switch (item.wantedStatus) {
    case "covered":
      return "covered";
    case "upgrade":
      return "upgrade";
    case "upcoming":
      return "upcoming";
    case "airing":
      return "airing";
    case "downloading":
      // Not the same as isTransferring above. That one is live telemetry — bytes
      // are moving right now. This is the stored fact that Deluno grabbed a
      // release and handed it to a client, which survives a restart and is true
      // in the hours before a torrent finds a peer.
      return "downloading";
    default:
      return "missing";
  }
}

/**
 * The subtitle languages you asked for, and how many of them are actually here.
 *
 * **The bar is subtitles on both media, and nothing else.** It used to be
 * subtitle languages on a movie and *aired episodes* on a show, which meant one
 * strip of pixels on one shelf answered two different questions depending on
 * which shelf you were looking at — and a show could never show its subtitle
 * state at all, because its bar was already spent.
 *
 * A movie is one file with the languages you asked for. A show is many files
 * with the same languages asked for of each, so it is the same sum:
 *
 *     movie:  held / wanted                       (1 file)
 *     show:   sum(held per episode) / (episodes you hold x wanted)
 *
 * **Counted only over the files you actually have.** Counting the episodes you
 * are missing would drag the bar down for a reason that has nothing to do with
 * subtitles, and the dot above it is already saying that — a show short of an
 * episode is Missing, in red, at the top of the same poster.
 *
 * Episode counts do not appear on a poster at all now. They are on the show's
 * own page, where the season list already lives.
 *
 * `wanted: 0` means nothing was asked for — every title, until Subber (#301).
 * The bar still appears, in grey, claiming nothing, so a shelf does not change
 * shape the day the numbers start arriving.
 */
export interface TitleBar {
  held: number;
  /**
   * Of `held`, how many are at the cutoff. Gold on the bar; the rest of `held`
   * is green.
   */
  settled: number;
  wanted: number;
  /** What the bar is counting, for the label a reader gets on hover. */
  noun: "subtitle languages";
}

/**
 * What each colour in the bar means, in the order a bar is read left to right.
 *
 * **One source, because there are now two readers.** The bar painted itself
 * from `--success` and `--destructive` written straight into a gradient, and
 * #327 asks for a legend that reads `TITLE_MARK_PRESENTATION`. Two places
 * naming the same two colours is the shape every defect in this codebase has
 * had, so the bar and its legend read this and nothing else.
 *
 * The marks are not arbitrary. DESIGN-002 settled that a subtitle bar is a
 * miniature of the dot's ladder using the dot's own colours — nothing new to
 * learn — so a segment *is* a `TitleMark`, and its colour comes from that
 * mark's own row.
 *
 * **Gold is absent on purpose.** `covered` would mean *at the cutoff, Deluno
 * has stopped looking*, and nothing can reach it until subtitle upgrades exist
 * (DESIGN-002 step 5: *"Two colours are enough until upgrades exist; gold
 * arrives with them."*). A legend listing a colour no bar can be is the same
 * defect as a filter chip that can never match.
 */
export const TITLE_BAR_SEGMENTS: readonly { mark: TitleMark; hint: string }[] = [
  {
    // Gold, and it arrived with the cutoff. DESIGN-002: "Two colours are enough
    // until upgrades exist; gold arrives with them." They exist — a subtitle is
    // at the cutoff when it was made for the file it sits beside, so the timing
    // is right and there is nothing better to find.
    mark: "covered",
    hint: "Made for this file, so the timing is right. Deluno has stopped looking."
  },
  {
    mark: "upgrade",
    // **Ready**, and it took two goes to get here. "Held" first — the word the
    // store uses for itself, leaked onto the screen. Then "Have", which is the
    // app's own voice but reads oddly as a label: a verb sitting next to an
    // adjective. "Ready" is parallel to "Missing", and it says the thing a
    // reader actually wants to know, which is that they can watch it tonight.
    //
    // Gold, when upgrades exist, is "Done" — so the set reads
    // Missing / Ready / Done, and each word says what the viewer can do rather
    // than what the store is holding.
    hint: "Here and watchable, and Deluno is still looking for one cut for your exact release."
  },
  {
    mark: "missing",
    hint: "A language you asked for is not here yet. Deluno looks for it on the library's own cycle."
  }
] as const;

/**
 * How much of a title is on disk, 0 to 1 — what the dot's ring is filled to.
 *
 * **Why a ring rather than a fifth word.** On TV the four rungs stop
 * discriminating: nearly every show is missing something, so nearly every
 * poster reads Missing, and three-of-twenty looks identical to none-of-eighty-
 * seven. A fifth rung would fix that at the cost of a new colour and a new word
 * on both shelves, when only one of them has the problem.
 *
 * The dot can already be drawn partially — that is what a half-grey dot means
 * for "not monitored" — so the fraction goes there. Same four rungs, same
 * colours, and the shape now says how far along.
 *
 * A movie is one file: it is either here or it is not, so it is always 0 or 1
 * and the ring is a plain dot.
 */
export function titleProgress(item: {
  hasFile?: boolean;
  airedEpisodeCount?: number;
  airedWithFileCount?: number;
}): number {
  const aired = item.airedEpisodeCount;
  if (typeof aired !== "number") {
    return item.hasFile === false ? 0 : 1;
  }

  // Nothing aired is a full ring, not an empty one. An Upcoming show is not
  // missing anything, and an empty ring would read as the worst possible state
  // when it is simply early.
  if (aired <= 0) return 1;

  return Math.min(1, Math.max(0, (item.airedWithFileCount ?? 0) / aired));
}

export function titleBar(item: {
  /**
   * Deliberately ignored. A show's aired count used to *be* the bar; it is on
   * the show's own page now, and accepting it here without reading it is what
   * stops a caller quietly re-introducing it.
   */
  airedEpisodeCount?: number;
  /** Held episodes, for a show. Absent on a movie. */
  airedWithFileCount?: number;
  /** Whether the title itself is on disk. A movie with no file holds nothing. */
  hasFile?: boolean;
  /** Languages asked for, per file. */
  subtitleLanguagesWanted?: number;
  /** Languages actually held, summed across the files the title has. */
  subtitleLanguagesHeld?: number;
  /** Of those, how many are at the cutoff. */
  subtitleLanguagesSettled?: number;
}): TitleBar {
  const perFile = Math.max(0, item.subtitleLanguagesWanted ?? 0);
  const held = Math.max(0, item.subtitleLanguagesHeld ?? 0);

  // A show is judged over the episodes it holds; a movie over its one file. The
  // `airedWithFileCount` field is what tells the two apart — a movie has none.
  const files = typeof item.airedWithFileCount === "number"
    ? Math.max(0, item.airedWithFileCount)
    : item.hasFile === false ? 0 : 1;

  const wanted = perFile * files;

  const heldCapped = Math.min(held, wanted);

  return {
    held: heldCapped,
    // Never longer than the green it sits inside, and never longer than the bar.
    // Clamping here rather than trusting the caller is what stops one bad number
    // from drawing a gold segment over a language nobody has.
    settled: Math.min(Math.max(0, item.subtitleLanguagesSettled ?? 0), heldCapped),
    wanted,
    noun: "subtitle languages"
  };
}

import { describe, expect, it } from "vitest";
import {
  STATUS_PRESENTATION,
  TITLE_MARK_PRESENTATION as MARKS,
  wantedStatusPresentation,
  TITLE_MARK_LADDER,
  TITLE_MARK_PRESENTATION,
  titleMark,
  TONE_MEANING,
  lowestMark,
  titleBar,
  type Tone
} from "./status-tones";

const TONES: Tone[] = ["ok", "warn", "info", "bad", "idle"];

describe("the one status table", () => {
  it("gives every state exactly one tone and one label", () => {
    for (const [key, presentation] of Object.entries(STATUS_PRESENTATION)) {
      expect(TONES, `${key} has an unknown tone`).toContain(presentation.tone);
      expect(presentation.label.trim(), `${key} has no label`).not.toBe("");
    }
  });

  it("says what every tone means, so a new state has something to be checked against", () => {
    for (const tone of TONES) {
      expect(TONE_MEANING[tone]?.trim()).toBeTruthy();
    }
  });

  /**
   * The heart of it. Amber is the only signal that means *you* have to do
   * something, and it is worth nothing the moment it also means "a job is
   * running" or "an indexer backed off and will resume by itself". Four places
   * spent it on work proceeding normally, which is how people learn to stop
   * reading it.
   *
   * This list is deliberately awkward to extend: adding a state to it should
   * require arguing that a person genuinely has to act.
   */
  it("spends amber only where a person has to do something", () => {
    const amber = Object.entries(STATUS_PRESENTATION)
      .filter(([, presentation]) => presentation.tone === "warn")
      .map(([key]) => key)
      .sort();

    expect(amber).toEqual([
      // Failed and out of retries. Nothing happens until someone looks.
      "connection.degraded",
      "job.deadLetter",
      // Stopped, and it will not restart itself.
      "transfer.needsALook",
      "transfer.stalled"
    ]);
  });

  /**
   * The specific regressions AUDIT-002 found, pinned so they cannot come back.
   * Each of these was a state that had been given a colour meaning the opposite
   * of what was happening.
   */
  it("keeps the states the audit found from drifting back", () => {
    // Was green in Transfers and grey in the pipeline strip. It is neither
    // finished nor idle — it is mid-pipeline.
    expect(STATUS_PRESENTATION["transfer.importReady"].tone).toBe("info");
    // Was amber. #290 names a running job blue by name.
    expect(STATUS_PRESENTATION["job.running"].tone).toBe("info");
    // Was grey. A queued job is motion that has not started.
    expect(STATUS_PRESENTATION["job.queued"].tone).toBe("info");
    // Was amber. Deluno backed off on purpose and resumes itself.
    expect(STATUS_PRESENTATION["connection.rateLimited"].tone).toBe("info");
    // Was green. Green is done; this is in motion.
    expect(STATUS_PRESENTATION["transfer.importing"].tone).toBe("info");
    // Was amber, and blue in the two other places that showed it.
    expect(STATUS_PRESENTATION["transfer.waitingForProcessor"].tone).toBe("info");
    // A configuration fact, not motion. Was blue.
    expect(STATUS_PRESENTATION["connection.categoryRoute"].tone).toBe("idle");
  });

  /**
   * A state that appears on three screens must be one entry, not three. Two
   * entries with the same label and different tones is exactly the defect the
   * table exists to prevent — it is how "ready to import" ended up green, grey
   * and blue at the same moment.
   */
  it("never gives one label two different colours", () => {
    const byLabel = new Map<string, Set<Tone>>();
    for (const presentation of Object.values(STATUS_PRESENTATION)) {
      const tones = byLabel.get(presentation.label) ?? new Set<Tone>();
      tones.add(presentation.tone);
      byLabel.set(presentation.label, tones);
    }

    const conflicts = [...byLabel.entries()]
      .filter(([, tones]) => tones.size > 1)
      .map(([label, tones]) => `${label}: ${[...tones].join(", ")}`);

    expect(conflicts).toEqual([]);
  });
});

describe("the mark on a title", () => {
  it("gives every rung a fill, a label and a hint", () => {
    for (const [mark, presentation] of Object.entries(TITLE_MARK_PRESENTATION)) {
      expect(presentation.dot, `${mark} has no fill`).toMatch(/^bg-/);
      expect(presentation.label.trim(), `${mark} has no label`).not.toBe("");
      expect(presentation.hint.trim(), `${mark} has no hint`).not.toBe("");
    }
  });

  /**
   * The half means the monitoring toggle and nothing else, so it only appears
   * where monitoring is deciding something now. A transfer under way finishes
   * regardless of monitoring, and a title that already has what you asked for
   * has left the lifecycle.
   */
  it("allows a half only where monitoring is still deciding something", () => {
    expect(TITLE_MARK_PRESENTATION.downloading.canBeHalf).toBe(false);
    expect(TITLE_MARK_PRESENTATION.covered.canBeHalf).toBe(false);
    expect(TITLE_MARK_PRESENTATION.missing.canBeHalf).toBe(true);
    expect(TITLE_MARK_PRESENTATION.upgrade.canBeHalf).toBe(true);
    expect(TITLE_MARK_PRESENTATION.upcoming.canBeHalf).toBe(true);
  });

  it("takes the lowest rung, so a show never overstates how well it is doing", () => {
    expect(lowestMark(["covered", "missing", "upgrade"])).toBe("missing");
    expect(lowestMark(["covered", "upgrade"])).toBe("upgrade");
    expect(lowestMark(["covered"])).toBe("covered");
    // Upcoming is off the ladder: an episode that has not aired says nothing
    // about how complete the aired ones are.
    expect(lowestMark(["upcoming"])).toBeNull();
    expect(lowestMark([])).toBeNull();
  });

  /**
   * The bar counts what you asked for beyond the title — episodes on a show,
   * subtitle languages on a film. A title that asked for nothing keeps a grey
   * bar that claims nothing, rather than none: the shelf must not change shape
   * the day Subber starts filling subtitle languages in.
   */
  it("counts aired episodes for a show and subtitle languages for a film", () => {
    expect(titleBar({ airedEpisodeCount: 18, airedWithFileCount: 13 }))
      .toEqual({ held: 13, wanted: 18, noun: "aired episodes" });

    expect(titleBar({ subtitleLanguagesWanted: 4, subtitleLanguagesHeld: 1 }))
      .toEqual({ held: 1, wanted: 4, noun: "subtitle languages" });
  });

  it("claims nothing when nothing was asked for", () => {
    // A film with no subtitle languages configured — every film, until #301.
    expect(titleBar({}).wanted).toBe(0);
    expect(titleBar({ airedEpisodeCount: 0 }).wanted).toBe(0);
  });

  it("never counts more held than asked for", () => {
    // An episode count that has shrunk under a stale file count would otherwise
    // draw a bar past its own end.
    expect(titleBar({ airedEpisodeCount: 3, airedWithFileCount: 9 }).held).toBe(3);
  });

  it("climbs in the order the design settled", () => {
    expect([...TITLE_MARK_LADDER]).toEqual(["missing", "downloading", "upgrade", "covered"]);
  });
});

/**
 * The mechanism, not just the table: a state cannot be coloured twice if only
 * one place colours it, so no screen may name a tone for a state of its own.
 *
 * Literal tones are still allowed for one-off chips that are not states — a
 * "Review before import" note, a rule name — because those have nothing to
 * drift against. What is forbidden is a screen deciding the colour of a state
 * the table already answers for.
 */
describe("no screen colours a state itself", () => {
  // Vite's own glob rather than node:fs, so this typechecks under the app's
  // tsconfig alongside everything else it is guarding.
  const SOURCES = import.meta.glob("../**/*.{ts,tsx}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;

  const ALLOWED = [
    "/lib/status-tones.ts",
    "/lib/status-tones.test.ts",
    "/lib/job-status-constants.ts",
    "/components/ui/chip.tsx",
    "/components/ui/status-led.tsx"
  ];

  const STATE_LABELS = new Set(
    Object.values(STATUS_PRESENTATION).map((presentation) => presentation.label.toLowerCase())
  );

  it("does not put a state's label next to a tone it chose itself", () => {
    const offenders: string[] = [];

    for (const [path, source] of Object.entries(SOURCES)) {
      const normalized = path.replace(/^\.\./, "");
      if (ALLOWED.some((allowed) => normalized.endsWith(allowed))) continue;

      // `tone="x"` or `tone: "x"` in the same expression as a state's label.
      const pattern = new RegExp(
        'tone[=:]\\s*"(ok|warn|info|bad|idle)"[^\\r\\n]*"([^"]+)"' +
          '|"([^"]+)"[^\\r\\n]*tone[=:]\\s*"(ok|warn|info|bad|idle)"',
        "g"
      );
      for (const match of source.matchAll(pattern)) {
        const label = (match[2] ?? match[3] ?? "").toLowerCase();
        if (STATE_LABELS.has(label)) {
          offenders.push(`${normalized}: "${label}" is coloured here rather than in STATUS_PRESENTATION`);
        }
      }
    }

    expect(offenders).toEqual([]);
  });
});

/**
 * The guard the first pass at this issue was missing.
 *
 * It watched for `tone="x"` beside a state's label — and every table that
 * survived #302's first attempt named its colours some other way. There were
 * four of them:
 *
 * - `MEDIA_STATUS_PRESENTATION`, a `bg-*`/`variant` map that coloured a missing
 *   title amber;
 * - `WANTED_STATUS_PRESENTATION`, which gave the same four wanted statuses a
 *   *second* set of tones — Missing blue here, red on the poster;
 * - `quickFilterConfig`, which wrote the mark colours out by hand three lines
 *   under a comment calling that row the legend;
 * - the two detail-page headers, which picked a Badge `variant` per status.
 *
 * So the rule is stated in the shape the offenders actually took: outside the
 * one table, no module may sit a mark's name next to a colour.
 */
describe("no second table", () => {
  const SOURCES = import.meta.glob("../**/*.{ts,tsx}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;

  const ALLOWED = ["/lib/status-tones.ts", "/lib/status-tones.test.ts"];

  const MARK_LABELS = Object.values(MARKS).map((presentation) => presentation.label);

  it("does not name a colour beside a mark's label", () => {
    // A Tailwind colour class or a Badge/Chip variant, on the same line as the
    // words the marks own.
    const COLOUR = /(bg-(destructive|success|info|warning|mark-[a-z-]+)|(variant|token)[=:]\s*"?(warning|success|info|destructive|mark-[a-z-]+))/;
    const offenders: string[] = [];

    for (const [path, source] of Object.entries(SOURCES)) {
      const normalized = path.replace(/^\.\./, "");
      if (ALLOWED.some((allowed) => normalized.endsWith(allowed))) continue;

      source.split("\n").forEach((line, index) => {
        if (!COLOUR.test(line)) return;
        for (const label of MARK_LABELS) {
          if (line.includes(`"${label}"`) || line.includes(`>${label}<`)) {
            offenders.push(`${normalized}:${index + 1} colours "${label}" itself`);
          }
        }
      });
    }

    expect(offenders).toEqual([]);
  });

  /**
   * The four stored wanted statuses and the five marks are one vocabulary, not
   * two that happen to agree today. `wantedStatusPresentation` must hand back
   * the very object `TITLE_MARK_PRESENTATION` holds — not a copy of it.
   */
  it("resolves a stored wanted status to the mark's own entry", () => {
    for (const stored of ["missing", "upgrade", "covered", "upcoming"] as const) {
      expect(wantedStatusPresentation(stored)).toBe(MARKS[titleMark({ wantedStatus: stored })]);
    }
  });

  /**
   * A value written by a newer build must not be read as Missing, which means
   * "go and download this". `titleMark` coerces it there because something has
   * to be drawn; the label a reader sees must not make that claim.
   */
  it("refuses to name a rung for a value it does not recognise", () => {
    for (const unknown of ["waiting", "", null, undefined, "brand-new-state"]) {
      expect(wantedStatusPresentation(unknown).label).toBe("Tracked");
    }
  });
});

/**
 * The guard the colour rule cannot be.
 *
 * A screen that invents its own *name* for a state slips past every check about
 * colour, because there is no mark label on the line to notice. The dashboard
 * did exactly that and survived two passes at #302: its opening strip counted
 * "Watching for", "Still missing" and "Could be upgraded", and the library ring
 * beside it drew "On disk", "Still missing" and **"Upgradeable"** — one letter
 * off the mark it was drawing, which is how you can tell it was written from
 * memory rather than read from the table.
 *
 * DESIGN-001 settled these names against real alternatives, and the reasoning is
 * recorded there. A retired one reappearing means somebody rebuilt a vocabulary
 * instead of importing it.
 */
describe("the retired words stay retired", () => {
  const SOURCES = import.meta.glob("../**/*.{ts,tsx}", { query: "?raw", import: "default", eager: true }) as Record<string, string>;

  const RETIRED: Array<[string, string]> = [
    ["Upgradeable", "Upgradable — one word, spelled the way the table spells it"],
    ["Still missing", "Missing"],
    ["Could be upgraded", "Upgradable"],
    ["Watching for it", "the mark, which already says whether Deluno is looking"],
    ["Best copy", "Quality met — Best copy over-claims (DESIGN-001)"],
    ["Upgrade needed", "Upgradable — it states a fact rather than nagging"]
  ];

  it("does not reintroduce a name DESIGN-001 replaced", () => {
    const offenders: string[] = [];

    for (const [path, source] of Object.entries(SOURCES)) {
      const normalized = path.replace(/^\.\./, "");
      // This file names them in order to forbid them.
      if (normalized.endsWith("/lib/status-tones.test.ts")) continue;

      source.split("\n").forEach((line, index) => {
        // Only user-facing strings and JSX text, not prose in a comment
        // explaining why the word went.
        if (/^\s*(\/\/|\*|\/\*)/.test(line)) return;
        for (const [word, instead] of RETIRED) {
          if (line.includes(`"${word}`) || line.includes(`>${word}<`)) {
            offenders.push(`${normalized}:${index + 1} says "${word}" — use ${instead}`);
          }
        }
      });
    }

    expect(offenders).toEqual([]);
  });
});

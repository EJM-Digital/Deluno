import type { MediaStatus } from "./media-types";

/**
 * The status shown on media is its lifecycle, never its monitoring intent.
 *
 * Monitoring answers "should Deluno keep looking?". It is deliberately kept
 * out of this presentation map so a missing monitored title remains amber
 * Missing, rather than looking healthy because it is being tracked.
 */
export const MEDIA_STATUS_PRESENTATION: Record<MediaStatus, {
  dot: string;
  compactLabel: string;
  label: string;
  tone: string;
  variant: "success" | "info" | "default" | "warning" | "destructive";
}> = {
  downloaded: { dot: "bg-success", compactLabel: "Ready", label: "Ready", tone: "border-success/30 bg-success/15 text-success", variant: "success" },
  downloading: { dot: "bg-info", compactLabel: "Downloading", label: "Downloading", tone: "border-info/30 bg-info/15 text-info", variant: "info" },
  processing: { dot: "bg-info", compactLabel: "Processing", label: "Processing", tone: "border-info/30 bg-info/15 text-info", variant: "info" },
  processed: { dot: "bg-success", compactLabel: "Processed", label: "Processed", tone: "border-success/30 bg-success/15 text-success", variant: "success" },
  waitingForProcessor: { dot: "bg-warning", compactLabel: "Waiting", label: "Waiting for processor", tone: "border-warning/30 bg-warning/15 text-warning", variant: "warning" },
  importReady: { dot: "bg-info", compactLabel: "Import ready", label: "Import ready", tone: "border-info/30 bg-info/15 text-info", variant: "info" },
  importQueued: { dot: "bg-info", compactLabel: "Import queued", label: "Import queued", tone: "border-info/30 bg-info/15 text-info", variant: "info" },
  importFailed: { dot: "bg-destructive", compactLabel: "Import failed", label: "Import failed", tone: "border-destructive/30 bg-destructive/15 text-destructive", variant: "destructive" },
  imported: { dot: "bg-success", compactLabel: "Imported", label: "Imported", tone: "border-success/30 bg-success/15 text-success", variant: "success" },
  processingFailed: { dot: "bg-destructive", compactLabel: "Processing failed", label: "Processing failed", tone: "border-destructive/30 bg-destructive/15 text-destructive", variant: "destructive" },
  missing: { dot: "bg-warning", compactLabel: "Missing", label: "Missing", tone: "border-warning/30 bg-warning/15 text-warning", variant: "warning" }
};

/** Colour rules for the reusable library summaries used by Movies and TV. */
export const LIBRARY_SUMMARY_PRESENTATION = {
  availability: {
    active: "text-success",
    empty: "text-muted-foreground"
  }
} as const;

export function librarySummaryTone(kind: keyof typeof LIBRARY_SUMMARY_PRESENTATION, count: number) {
  const presentation = LIBRARY_SUMMARY_PRESENTATION[kind];
  return count > 0 ? presentation.active : presentation.empty;
}

export function mediaStatusIsActive(status: MediaStatus) {
  return status === "downloading" || status === "processing" || status === "waitingForProcessor" || status === "importQueued";
}

/**
 * What Deluno wants for a title, in the user's words rather than the engine's.
 *
 * Four stored values, one meaning each — see `DESIGN-001-title-marks.md` and
 * #300. Until that split there were three, and one of them said three different
 * things: `waiting` was set by the server on a title that *has* a file and
 * already meets its target, and described here as "not searchable yet — it has
 * not been released", which is the opposite state. Anyone reading that tooltip
 * was told nothing had been imported precisely when something had.
 *
 * The names are the ones settled in DESIGN-001. *Upgradable* states a fact
 * rather than nagging, and is the only one whose count is worth reading —
 * "3 upgradable" is a to-do list. *Quality met* rather than "Best copy", which
 * over-claims: it is not the best copy in existence, it is the one your profile
 * asked for.
 */
export const WANTED_STATUS_PRESENTATION: Record<string, { label: string; tone: "ok" | "warn" | "info" | "muted"; hint: string }> = {
  missing: {
    label: "Missing",
    tone: "info",
    hint: "It is out and Deluno does not have it yet. Deluno searches for this on its schedule."
  },
  upgrade: {
    label: "Upgradable",
    tone: "info",
    hint: "You have this and can watch it tonight. Deluno is still looking for a better copy."
  },
  covered: {
    label: "Quality met",
    tone: "ok",
    hint: "This is the quality your Library Profile asked for, so Deluno has stopped looking."
  },
  upcoming: {
    label: "Upcoming",
    tone: "muted",
    hint: "Not out yet. Deluno will start looking on release."
  }
};

export function wantedStatusPresentation(value: string) {
  return (
    WANTED_STATUS_PRESENTATION[value] ?? {
      label: "Tracked",
      tone: "muted" as const,
      // Reached only by a value this build does not know — a database written by
      // a newer one, say. It must not claim a state; "tracked" is the most it
      // can support from the fact that a row exists at all.
      hint: "Deluno holds this title but does not recognise its current state."
    }
  );
}

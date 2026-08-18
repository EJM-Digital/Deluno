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
 * The stored values are `covered` / `missing` / `upgrade` / `waiting`. "Covered"
 * is engine vocabulary — it means Deluno has a file that already satisfies the
 * media plan, so it has stopped looking. The label says that; the hint says why,
 * because a status word on its own is only ever half an explanation.
 */
export const WANTED_STATUS_PRESENTATION: Record<string, { label: string; tone: "ok" | "warn" | "info" | "muted"; hint: string }> = {
  covered: {
    label: "Complete",
    tone: "ok",
    hint: "Deluno has a file that meets your media plan, so it has stopped looking."
  },
  missing: {
    label: "Missing",
    tone: "warn",
    hint: "Nothing has been imported yet. Deluno searches for this on its schedule."
  },
  upgrade: {
    label: "Upgrade wanted",
    tone: "info",
    hint: "There is a file, but it is below the quality your media plan asks for."
  },
  waiting: {
    label: "Waiting",
    tone: "muted",
    hint: "Not searchable yet — it has not been released, or a retry window is still open."
  }
};

export function wantedStatusPresentation(value: string) {
  return (
    WANTED_STATUS_PRESENTATION[value] ?? {
      label: "Tracked",
      tone: "muted" as const,
      hint: "Deluno is keeping an eye on this title."
    }
  );
}

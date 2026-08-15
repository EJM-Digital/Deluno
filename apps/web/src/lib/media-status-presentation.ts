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

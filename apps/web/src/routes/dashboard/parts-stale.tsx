import type { JSX } from "react";

/**
 * Said when a read behind a board that is already showing failed: the board keeps what it has and this small note, drawn over the
 * corner of the page and out of its flow, says it may be out of date. It pushes nothing, and it goes when the next read works (#1508).
 */
export function StaleNote({ show }: { show: boolean }): JSX.Element | null {
  if (!show) return null;
  return (
    <p role="status" data-stale-note="" className="pointer-events-none absolute right-2 bottom-1 z-10 rounded-full border border-warning/40 bg-card px-2.5 py-0.5 text-(length:--type-caption) text-warning-foreground shadow-card">
      Couldn’t refresh just now. Showing what was last read.
    </p>
  );
}

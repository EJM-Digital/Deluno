/**
 * SummaryStrip — the one "state of this area, right now" block.
 *
 * A single card-height row of read-only cells that answers "is this working?"
 * before the list below answers "what is in it?". It is deliberately not a
 * ListCard: nothing in it is a row, nothing opens a drawer, nothing is editable.
 *
 *   ┌ label (11 uppercase) ┐
 *   │ value (15 semibold)  │  × 2–5, one border-separated cell each
 *   └ help  (12.5 muted)   ┘
 *
 * Sits directly under the PageToolbar. Wraps to two columns on narrow screens.
 */
import * as React from "react";
import { Link } from "react-router-dom";
import { cn } from "../../lib/utils";

export interface SummaryCell {
  label: React.ReactNode;
  value: React.ReactNode;
  /** One short line under the value. Say what the number means, not what it is. */
  help?: React.ReactNode;
  /** Colours the value only — the strip never changes its own background. */
  tone?: "warning" | "danger" | "success";
  /**
   * Where this number is dealt with. A summary that states a problem should
   * lead to the page that fixes it rather than leaving the reader to find it.
   */
  href?: string;
  /** 0–1. A proportion the number alone cannot show, such as free space. */
  fill?: number;
}

const toneClass: Record<NonNullable<SummaryCell["tone"]>, string> = {
  warning: "text-warning",
  danger: "text-destructive",
  success: "text-success"
};

const barClass: Record<NonNullable<SummaryCell["tone"]>, string> = {
  warning: "bg-warning",
  danger: "bg-destructive",
  success: "bg-success"
};

export function SummaryStrip({ cells, className }: { cells: SummaryCell[]; className?: string }) {
  return (
    <div
      className={cn(
        "grid grid-cols-2 overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]",
        cells.length >= 6 ? "md:grid-cols-6" : cells.length === 5 ? "md:grid-cols-5" : cells.length === 4 ? "md:grid-cols-4" : cells.length === 3 ? "md:grid-cols-3" : "md:grid-cols-2",
        className
      )}
    >
      {cells.map((cell, index) => {
        const body = (
          <>
            <span className="block text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
              {cell.label}
            </span>
            <span
              className={cn(
                "mt-1 block text-[length:var(--type-title-sm)] font-semibold tabular-nums leading-tight",
                cell.tone ? toneClass[cell.tone] : "text-foreground"
              )}
            >
              {cell.value}
            </span>
            {cell.help !== undefined ? (
              <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{cell.help}</span>
            ) : null}
            {cell.fill !== undefined ? (
              <span aria-hidden className="mt-1.5 block h-1 w-full overflow-hidden rounded-full bg-surface-3">
                <span
                  className={cn("block h-full rounded-full", cell.tone ? barClass[cell.tone] : "bg-primary")}
                  style={{ width: `${Math.min(100, Math.max(0, cell.fill * 100))}%` }}
                />
              </span>
            ) : null}
          </>
        );

        const cellClass = cn(
          "border-hairline px-[var(--card-pad-x)] py-3",
          // Cells divide left-to-right on wide screens and form a grid on narrow ones,
          // so the trailing edges never draw a border against the card edge.
          "border-b border-r last:border-r-0 md:border-b-0",
          index === cells.length - 2 && cells.length % 2 === 0 && "md:border-r"
        );

        return cell.href ? (
          <Link
            key={index}
            to={cell.href}
            className={cn(
              cellClass,
              "block transition-colors hover:bg-primary/[0.06] focus-visible:relative focus-visible:z-10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
            )}
          >
            {body}
          </Link>
        ) : (
          <div key={index} className={cellClass}>
            {body}
          </div>
        );
      })}
    </div>
  );
}

/**
 * The one template of the top band (#1098): Speed, Working on now and Next are
 * three cards of the same size and parts, in the same places: a label row (the
 * name on the left, a quiet aside on the right), a big figure row, and a body that
 * takes the rest. Sizes come from the app's type scale, so the three line up at
 * every window size.
 */
import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { cn } from "../../../lib/utils";

export function BandCard({ label, aside, figure, to, linkName, children, className }: {
  label: string;
  /** The quiet note at the right of the label row. */
  aside?: ReactNode;
  /** The big figure: a number with its unit, and a few small words beside it. */
  figure: ReactNode;
  to?: string;
  /** The accessible name of the label's link: "Transfers: Speed". */
  linkName?: string;
  children: ReactNode;
  className?: string;
}) {
  const labelClass = "text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.12em] text-muted-foreground";
  return (
    <section aria-label={label} className={cn("grid min-h-0 min-w-0 grid-rows-[auto_auto_minmax(0,1fr)] gap-y-1.5 overflow-hidden rounded-2xl border border-hairline bg-card px-[var(--card-pad-x)] py-[var(--card-pad-y)] shadow-card dark:border-white/[0.07]", className)}>
      <div className="flex items-baseline justify-between gap-2">
        {to ? <Link to={to} aria-label={linkName} className={cn(labelClass, "hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring")}>{label}</Link> : <span className={labelClass}>{label}</span>}
        {aside ? <span className="min-w-0 truncate text-[length:var(--type-caption)] tabular-nums text-muted-foreground">{aside}</span> : null}
      </div>
      <div className="flex min-w-0 items-baseline gap-x-2 overflow-hidden whitespace-nowrap text-[length:var(--type-title-md)] font-bold leading-[1.15] tracking-tight tabular-nums text-foreground">{figure}</div>
      <div className="min-h-0 min-w-0">{children}</div>
    </section>
  );
}

/** The small words after a figure: "MB/s down", "things running". */
export function FigureUnit({ children }: { children: ReactNode }) {
  return <span className="min-w-0 truncate text-[length:var(--type-body-sm)] font-medium tracking-normal text-muted-foreground">{children}</span>;
}

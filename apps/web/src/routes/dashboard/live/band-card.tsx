/**
 * The one template of the top band (#1098): Speed, Working on now and Next are
 * three cards of the same size and parts, in the same places, ported from the
 * mockup (band.css): a label row (the name on the left, a quiet aside on the
 * right), a big figure row, and a body that takes the rest.
 */
import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { cn } from "../../../lib/utils";
import "./band.css";

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
  return (
    <section aria-label={label} className={cn("lb-tile", className)}>
      <div className="lb-top">
        {to ? <Link to={to} aria-label={linkName} className="lb-label focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">{label}</Link> : <span className="lb-label">{label}</span>}
        {aside ? <span className="lb-aside">{aside}</span> : null}
      </div>
      <div className="lb-fig">{figure}</div>
      <div className="lb-body">{children}</div>
    </section>
  );
}

/** The small words after a figure: "MB/s down", "things running". */
export function FigureUnit({ children }: { children: ReactNode }) {
  return <small>{children}</small>;
}

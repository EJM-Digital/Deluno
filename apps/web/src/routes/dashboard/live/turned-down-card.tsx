/**
 * Turned down today (#1098, the approved mockup like for like): how many releases Deluno looked at and did not take, as one
 * bar per plain reason and nothing else. Biggest first; a reason with nothing in it stays as a quiet empty track so the card
 * always reads the whole picture; whole rows only, then "and N more reasons". A bar flashes when a decision is made.
 *
 * Every bar is a link to Activity filtered to that reason today, and the card's own link to Activity filtered to today's
 * turned-down releases (see `lib/activity-history.ts`).
 */
import { useLayoutEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { cn } from "../../../lib/utils";
import { LiveCard as Panel } from "./live-card";
import "./turned-down.css";
import { useElementSize } from "./hooks";
import { activityLink } from "../../../lib/activity-history";
import { REASON_COLOUR, REASON_LABEL, barFraction, reasonsFit, type TurnedDownView } from "./turned-down";

/** The line under the bars: how many reasons are not shown and what they hold, or that they hold nothing today. */
export function moreLine(reasons: number, releases: number): string {
  const head = `and ${reasons.toLocaleString("en-AU")} more ${reasons === 1 ? "reason" : "reasons"}`;
  if (releases <= 0) return `${head}, none today`;
  return `${head} · ${releases.toLocaleString("en-AU")} ${releases === 1 ? "release" : "releases"}`;
}

export function TurnedDownCard({ view, lit }: { view: TurnedDownView; lit: ReadonlySet<string> }) {
  const [boxRef, box] = useElementSize<HTMLDivElement>();
  const firstRef = useRef<HTMLAnchorElement | null>(null);
  const [rowHeight, setRowHeight] = useState(0);
  // The row height is read from the first bar as drawn (fonts and zoom change it), before paint, so the rows that fit are whole ones.
  useLayoutEffect(() => {
    const height = firstRef.current?.offsetHeight ?? 0;
    setRowHeight((current) => (current === height ? current : height));
  });
  const fit = reasonsFit(box.height, rowHeight, view.reasons.length);
  const hidden = view.reasons.slice(fit);
  const count = view.total === 1 ? "1 release" : `${view.total.toLocaleString("en-AU")} releases`;
  return (
    <Panel title="Turned down today" count={count} to={activityLink({ type: "turned-down", since: "today" })} toLabel="In Activity" className="ltd min-h-0 flex-1" bodyClassName="min-h-0">
      <div ref={boxRef} className="ltd-bars">
        {view.reasons.slice(0, fit).map((reason, index) => {
          const label = reason.label || REASON_LABEL[reason.group];
          return (
            <Link
              key={reason.group}
              ref={index === 0 ? firstRef : undefined}
              to={activityLink({ type: "turned-down", since: "today", reason: reason.group })}
              title={`Show today's "${label}" in Activity`}
              aria-label={`${label}: ${reason.count.toLocaleString("en-AU")} today, show in Activity`}
              className={cn("ltd-br", reason.count === 0 && "zero")}
              data-reason={reason.group}
              data-hit={lit.has(reason.group) ? "" : undefined}
              style={{ ["--c" as string]: REASON_COLOUR[reason.group] }}
            >
              <span>{label}</span>
              <span className="ltd-num">{reason.count.toLocaleString("en-AU")}</span>
              <span aria-hidden className="ltd-tr"><i style={{ width: `${Math.round(barFraction(reason.count, view.reasons) * 100)}%` }} /></span>
            </Link>
          );
        })}
        {hidden.length > 0 ? (
          <Link to={activityLink({ type: "turned-down", since: "today" })} className="ltd-more">
            {moreLine(hidden.length, hidden.reduce((sum, reason) => sum + reason.count, 0))}
          </Link>
        ) : null}
      </div>
    </Panel>
  );
}

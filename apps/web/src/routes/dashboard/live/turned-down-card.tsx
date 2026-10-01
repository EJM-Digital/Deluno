/**
 * Turned down today (#1098): how many releases Deluno looked at and did not take,
 * as a bar per plain reason, and where there is room the newest turned-down
 * releases with their reason (whole rows only: a row that does not fit is not drawn). A bar flashes when a decision is made. The newest
 * releases are the stored ones with every pushed one on top, so the card is already
 * right when the next count is read.
 */
import { useLayoutEffect, useState } from "react";
import { Link } from "react-router-dom";
import { cn } from "../../../lib/utils";
import { LiveCard as Panel } from "./live-card";
import "./turned-down.css";
import { useElementSize, useIsWide, useRemPx } from "./hooks";
import { activityLink } from "../../../lib/activity-history";
import { REASON_COLOUR, REASON_LABEL, barFraction, turnedDownFit, type TurnedDownView } from "./turned-down";

export function TurnedDownCard({ view, lit }: { view: TurnedDownView; lit: ReadonlySet<string> }) {
  const [boxRef, box] = useElementSize<HTMLDivElement>();
  const rem = useRemPx();
  // Held to the window on a wide screen, the card shows the whole rows its height holds; on a phone the page scrolls and it shows them all.
  const estimate = turnedDownFit(useIsWide() ? box.height : 0, rem, view.reasons.length, view.latest.length);
  // The estimate can be a few pixels out (fonts, wrapping): after drawing, a card whose rows still run past its height
  // drops a whole row, from the newest list first and then the last reason, so nothing is ever drawn half cut.
  const [trim, setTrim] = useState(0);
  useLayoutEffect(() => setTrim(0), [box.height, view.latest.length, view.reasons.length]);
  const fromLatest = Math.min(trim, estimate.latest);
  const fit = { latest: estimate.latest - fromLatest, reasons: Math.max(1, estimate.reasons - (trim - fromLatest)) };
  useLayoutEffect(() => {
    const holder = boxRef.current;
    if (!holder || !(box.height > 0) || fit.latest + fit.reasons <= 1) return;
    if (holder.scrollHeight > holder.clientHeight + 1) setTrim((value) => value + 1);
  });
  // Nothing turned down: the bars are quiet empty tracks, spread over the card so it is never a big empty box under them.
  const quiet = view.total === 0 && fit.latest === 0;
  const count = view.total === 1 ? "1 release" : `${view.total.toLocaleString("en-AU")} releases`;
  return (
    <Panel title="Turned down today" count={count} to={activityLink({ type: "turned-down", since: "today" })} toLabel="See them all" className="ltd min-h-0" bodyClassName="min-h-0">
      <div ref={boxRef} className="flex min-h-0 flex-1 flex-col overflow-hidden">
        <ul aria-label="Reasons" className={cn("ltd-bars", quiet && "quiet")}>
          {view.reasons.slice(0, fit.reasons).map((reason) => {
            const hit = lit.has(reason.group);
            return (
              <li key={reason.group} className="ltd-br" data-reason={reason.group} data-hit={hit ? "" : undefined} style={{ ["--c" as string]: REASON_COLOUR[reason.group] }}>
                <Link to={activityLink({ type: "turned-down", since: "today", reason: reason.group })} aria-label={`${reason.label || REASON_LABEL[reason.group]}: ${reason.count.toLocaleString("en-AU")}. See them in Activity`}>
                  <span>{reason.label || REASON_LABEL[reason.group]}</span>
                  <span className="ltd-num">{reason.count.toLocaleString("en-AU")}</span>
                  <span aria-hidden className="ltd-tr"><i style={{ width: `${Math.round(barFraction(reason.count, view.reasons) * 100)}%` }} /></span>
                </Link>
              </li>
            );
          })}
        </ul>
        {fit.latest > 0 ? (
          <ul aria-label="Newest turned down" data-testid="live-turned-down-latest" className="ltd-rej">
            {view.latest.slice(0, fit.latest).map((item) => {
              const to = item.entityId ? (item.entityType === "movie" ? `/movies/${item.entityId}` : `/tv/${item.entityId}`) : "/search-cycles";
              return (
                <li key={`${item.atUtc}:${item.release}`} style={{ ["--c" as string]: REASON_COLOUR[item.group] }}>
                  <Link to={to} title={item.title ? `${item.title}: ${item.release}` : item.release}>
                    <i aria-hidden />
                    <span className="ltd-rn">{item.release}</span>
                    <span className="ltd-rw">{item.label}</span>
                  </Link>
                </li>
              );
            })}
          </ul>
        ) : fit.reasons === view.reasons.length && view.latest.length === 0 ? (
          <p className="ltd-note">{view.total === 0 ? "Nothing has been turned down yet today." : "The newest ones are in the counts above."}</p>
        ) : null}
      </div>
    </Panel>
  );
}

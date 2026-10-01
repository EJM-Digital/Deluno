/**
 * Turned down today (#1098): how many releases Deluno looked at and did not take,
 * as a bar per plain reason, and where there is room the newest turned-down
 * releases with their reason (whole rows only: a row that does not fit is not drawn). A bar flashes when a decision is made. The newest
 * releases are the stored ones with every pushed one on top, so the card is already
 * right when the next count is read.
 */
import { Link } from "react-router-dom";
import { cn } from "../../../lib/utils";
import { LiveCard as Panel } from "./live-card";
import { REASON_COLOUR, REASON_LABEL, barFraction, type TurnedDownView } from "./turned-down";

export function TurnedDownCard({ view, lit }: { view: TurnedDownView; lit: ReadonlySet<string> }) {
  const count = view.total === 1 ? "1 release" : `${view.total.toLocaleString("en-AU")} releases`;
  return (
    <Panel title="Turned down today" count={count} className="min-h-0" bodyClassName="min-h-0">
      <div className="grid min-h-0 flex-1 grid-rows-[auto_minmax(0,1fr)]">
        <ul aria-label="Reasons" className="grid gap-2 px-[var(--card-pad-x)] pb-3 pt-3">
          {view.reasons.map((reason) => {
            const hit = lit.has(reason.group);
            return (
              <li key={reason.group} data-reason={reason.group} data-hit={hit ? "" : undefined} className="grid grid-cols-[minmax(0,1fr)_auto] gap-x-2.5 gap-y-1 text-[length:var(--type-caption)] text-foreground/90" style={{ ["--reason" as string]: REASON_COLOUR[reason.group] }}>
                <span className="truncate">{reason.label || REASON_LABEL[reason.group]}</span>
                <span className={cn("font-semibold tabular-nums transition-colors duration-300", hit ? "text-[color:var(--reason)]" : "text-foreground")}>{reason.count.toLocaleString("en-AU")}</span>
                <span aria-hidden className="col-span-2 block h-1.5 overflow-hidden rounded-full bg-surface-3">
                  <span className="block h-full rounded-full bg-[color:var(--reason)] transition-[width,box-shadow] duration-700" style={{ width: `${Math.round(barFraction(reason.count, view.reasons) * 100)}%`, boxShadow: hit ? "0 0 12px var(--reason)" : undefined }} />
                </span>
              </li>
            );
          })}
        </ul>
        {view.latest.length > 0 ? (
          <ul aria-label="Newest turned down" className="flex min-h-0 flex-col flex-wrap content-start overflow-hidden border-t border-hairline px-[var(--card-pad-x)] py-1">
            {view.latest.map((item) => {
              const to = item.entityId ? (item.entityType === "movie" ? `/movies/${item.entityId}` : `/tv/${item.entityId}`) : "/search-cycles";
              return (
                <li key={`${item.atUtc}:${item.release}`} className="w-full border-b border-hairline py-1.5" style={{ ["--reason" as string]: REASON_COLOUR[item.group] }}>
                  <Link to={to} title={item.title ? `${item.title}: ${item.release}` : item.release} className="grid grid-cols-[0.5rem_minmax(0,1fr)] items-baseline gap-x-2.5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                    <span aria-hidden className="h-2 w-2 rounded-full bg-[color:var(--reason)]" />
                    <span className="truncate font-mono text-[length:var(--type-micro)] text-foreground">{item.release}</span>
                    <span className="col-start-2 text-[length:var(--type-micro)] text-muted-foreground">{item.label}</span>
                  </Link>
                </li>
              );
            })}
          </ul>
        ) : (
          <p className="border-t border-hairline px-[var(--card-pad-x)] py-2.5 text-[length:var(--type-caption)] text-muted-foreground">{view.total === 0 ? "Nothing has been turned down yet today." : "The newest ones are in the counts above."}</p>
        )}
      </div>
    </Panel>
  );
}

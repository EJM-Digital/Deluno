/**
 * What Deluno just did (#1096), as a live stream: an icon and a colour for the
 * kind of thing (a search, a grab, an import, a problem, a clean-up,
 * retention, subtitles, lists and requests), the plain sentence, and when and
 * where. A row that arrives while the screen is open slides in and glows once.
 */
import { useMemo, type ReactElement } from "react";
import { Link } from "react-router-dom";
import { eventLabel } from "../../../lib/activity-format";
import { motionAllowed } from "../../../lib/page-motion";
import { cn } from "../../../lib/utils";
import { ago } from "../format";
import type { LiveActivityRow } from "../live-events";
import { FitList, Panel, Plain } from "../parts";
import { activityKind, isRoutine, KIND_STYLE } from "./kinds";

function entityLink(item: Pick<LiveActivityRow, "relatedEntityType" | "relatedEntityId">): string {
  const kind = item.relatedEntityType?.toLowerCase() ?? "";
  if (!item.relatedEntityId) return "/activity";
  if (kind.includes("movie")) return `/movies/${item.relatedEntityId}`;
  if (kind.includes("series") || kind.includes("show")) return `/tv/${item.relatedEntityId}`;
  return "/activity";
}

function renderRow(item: LiveActivityRow): ReactElement {
  const kind = KIND_STYLE[activityKind(item.category, item.severity)];
  return (
    <li key={item.id} className={cn("border-b border-hairline last:border-b-0", item.fresh && motionAllowed() && "live-stream-new")}>
      <Link
        to={entityLink(item)}
        className="flex items-start gap-2.5 px-[var(--card-pad-x)] py-2 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        <span aria-hidden className={cn("mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg", kind.tile, kind.text)}>
          <kind.icon className="h-4 w-4" />
        </span>
        <span className="min-w-0 flex-1">
          <span className="block text-[length:var(--type-body-sm)] leading-snug text-foreground">{item.message}</span>
          <span className="block text-[length:var(--type-caption)] text-muted-foreground">
            <span className={kind.text}>{eventLabel(item)}</span> &middot; {ago(item.createdUtc)}
          </span>
        </span>
      </Link>
    </li>
  );
}

export function ActivityStream({ rows }: { rows: LiveActivityRow[] }) {
  // What Deluno did for a title, not its routine housekeeping: that is one quiet count.
  const { shown, routine } = useMemo(() => {
    const mattering: LiveActivityRow[] = [];
    let quiet = 0;
    for (const row of rows) {
      if (isRoutine(activityKind(row.category, row.severity))) quiet += 1;
      else mattering.push(row);
    }
    return { shown: mattering, routine: quiet };
  }, [rows]);
  return (
    <Panel title="Latest" count="what Deluno just did" to="/activity" toLabel="All activity">
      {shown.length === 0 ? (
        <Plain>Nothing has happened yet. What Deluno does shows up here as it works.</Plain>
      ) : (
        <FitList moreTo="/activity" rows={shown.map(renderRow)} />
      )}
      {routine > 0 ? (
        <Link to="/activity" className="shrink-0 border-t border-hairline px-[var(--card-pad-x)] py-1.5 text-[length:var(--type-caption)] text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
          and {routine.toLocaleString("en-AU")} routine background {routine === 1 ? "event" : "events"}, left out
        </Link>
      ) : null}
    </Panel>
  );
}

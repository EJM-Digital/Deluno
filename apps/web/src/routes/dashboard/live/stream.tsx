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
import { Plain } from "../parts";
import { LiveCard as Panel } from "./live-card";
import { groupActivity, type ActivityGroup } from "./group";
import { activityKind, isRoutine, KIND_STYLE } from "./kinds";
import { entityFromRelated, namedTitle, type TitleLookup } from "./titles";

export { sentenceOf } from "./group";

/** More rows than any screen shows; the fade hides the rest. */
const MAX_ROWS = 14;

function entityLink(item: Pick<LiveActivityRow, "relatedEntityType" | "relatedEntityId">, titles: TitleLookup): string {
  const named = namedTitle(entityFromRelated(item.relatedEntityType, item.relatedEntityId), titles);
  if (named) return named.to;
  const kind = item.relatedEntityType?.toLowerCase() ?? "";
  if (!item.relatedEntityId) return "/activity";
  if (kind.includes("movie")) return `/movies/${item.relatedEntityId}`;
  if (kind.includes("series") || kind.includes("show")) return `/tv/${item.relatedEntityId}`;
  return "/activity";
}

function renderRow(group: ActivityGroup, titles: TitleLookup, dense: boolean, newest: boolean): ReactElement {
  const item = group.first;
  const kind = KIND_STYLE[activityKind(item.category, item.severity)];
  const many = group.rows.length;
  return (
    <li key={item.id} className={cn("shrink-0 rounded-[10px]", newest && "bg-info/[0.09]", group.fresh && motionAllowed() && "live-stream-new")}>
      <Link
        to={entityLink(item, titles)}
        title={many > 1 ? group.detail.join("\n") : undefined}
        className={cn("flex h-full items-center gap-2.5 rounded-[10px] px-2 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring", dense ? "py-1" : "py-[9px]")}
      >
        <span aria-hidden className={cn("flex shrink-0 items-center justify-center rounded-lg", dense ? "h-6 w-6" : "h-8 w-8 rounded-[9px]", kind.tile, kind.text)}>
          <kind.icon className={dense ? "h-3.5 w-3.5" : "h-[18px] w-[18px]"} />
        </span>
        {dense ? (
          <>
            <span title={`${group.sentence} (${eventLabel(item)})`} className="min-w-0 flex-1 truncate text-[length:var(--type-body-sm)] text-foreground">{group.sentence}</span>
            <span className="shrink-0 text-[length:var(--type-caption)] tabular-nums text-muted-foreground">{many > 1 ? `${many.toLocaleString("en-AU")} times · ` : null}{ago(item.createdUtc)}</span>
          </>
        ) : (
          <span className="min-w-0 flex-1">
            <span title={group.sentence} className="line-clamp-2 text-[length:var(--type-body-sm)] leading-snug text-foreground">{group.sentence}</span>
            <span className="mt-0.5 block text-[length:var(--type-caption)] text-muted-foreground">
              <span className={kind.text}>{eventLabel(item)}</span>
              {many > 1 ? <span className={cn("ml-1.5 rounded-full px-1.5 py-px text-[length:var(--type-micro)] font-semibold tabular-nums", kind.tile, kind.text)}>{many.toLocaleString("en-AU")} times</span> : null} &middot; {ago(item.createdUtc)}
            </span>
          </span>
        )}
      </Link>
    </li>
  );
}

export function ActivityStream({ rows, titles, dense = false }: { rows: LiveActivityRow[]; titles: TitleLookup; /** One-line rows, when the tiles above have been made compact to leave Activity its room. */ dense?: boolean; }) {
  // What Deluno did for a title, not its routine housekeeping: that is one quiet count.
  const { shown, routine } = useMemo(() => {
    const mattering: LiveActivityRow[] = [];
    let quiet = 0;
    for (const row of rows) {
      if (isRoutine(activityKind(row.category, row.severity))) quiet += 1;
      else mattering.push(row);
    }
    // Neighbours that say the same thing are one line with a count.
    return { shown: groupActivity(mattering, titles, (row) => ago(row.createdUtc)), routine: quiet };
  }, [rows, titles]);
  return (
    <Panel title="Activity" count={routine > 0 ? `${routine.toLocaleString("en-AU")} routine left out` : "as it happens"} to="/activity" toLabel="All activity" className="min-h-[10rem] flex-1">
      {shown.length === 0 ? (
        <Plain>Nothing has happened yet. What Deluno does shows up here as it works.</Plain>
      ) : (
        // The newest on top, and the stream fades at the bottom instead of cutting a row: the rest is one click away.
        <ul data-testid="live-stream" className="flex min-h-0 flex-1 flex-col overflow-hidden px-2.5 pb-2.5 pt-0.5 [mask-image:linear-gradient(#000_calc(100%-3.5rem),transparent)]">
          {shown.slice(0, MAX_ROWS).map((group, index) => renderRow(group, titles, dense, index === 0))}
        </ul>
      )}
    </Panel>
  );
}

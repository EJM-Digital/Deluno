/**
 * What Deluno just did (#1096), as a live stream: an icon and a colour for the
 * kind of thing (a search, a grab, an import, a problem, a clean-up,
 * retention, subtitles, lists and requests), the plain sentence, and when and
 * where. A row that arrives while the screen is open slides in and glows once.
 */
import { useMemo, type ReactElement, type RefObject } from "react";
import { Link } from "react-router-dom";
import { eventLabel } from "../../../lib/activity-format";
import { activityLinkForStatus } from "../../../lib/activity-history";
import { motionAllowed } from "../../../lib/page-motion";
import { cn } from "../../../lib/utils";
import { ago } from "../format";
import type { LiveActivityRow } from "../live-events";
import { Plain } from "../parts";
import { LiveCard as Panel } from "./live-card";
import "./stream.css";
import { useFittingRows } from "./fit-rows";
import { groupActivity, type ActivityGroup } from "./group";
import { activityKind, isRoutine, KIND_STYLE } from "./kinds";
import { entityFromRelated, namedTitle, type TitleLookup } from "./titles";

export { sentenceOf } from "./group";

/** The most entries the stream lays out; it shows the whole ones that fit and says how many more (the owner, 2 Oct 2026, as Weir). */
const MAX_ROWS = 20;
/** This feed is the history of everything, so a row about no title in particular, and "All activity", open All. */
const ACTIVITY_HISTORY = activityLinkForStatus();

function entityLink(item: Pick<LiveActivityRow, "relatedEntityType" | "relatedEntityId">, titles: TitleLookup): string {
  const named = namedTitle(entityFromRelated(item.relatedEntityType, item.relatedEntityId), titles);
  if (named) return named.to;
  const kind = item.relatedEntityType?.toLowerCase() ?? "";
  if (!item.relatedEntityId) return ACTIVITY_HISTORY;
  if (kind.includes("movie")) return `/movies/${item.relatedEntityId}`;
  if (kind.includes("series") || kind.includes("show")) return `/tv/${item.relatedEntityId}`;
  return ACTIVITY_HISTORY;
}

/** The sentence with the title in bold, where the sentence says it. */
export function boldTitle(sentence: string, name: string | null | undefined): ReactElement | string {
  const at = name ? sentence.indexOf(name) : -1;
  if (!name || at < 0) return sentence;
  return <>{sentence.slice(0, at)}<b>{name}</b>{sentence.slice(at + name.length)}</>;
}

function renderRow(group: ActivityGroup, titles: TitleLookup, dense: boolean, newest: boolean): ReactElement {
  const item = group.first;
  const kind = KIND_STYLE[activityKind(item.category, item.severity)];
  const many = group.rows.length;
  const named = namedTitle(entityFromRelated(item.relatedEntityType, item.relatedEntityId), titles);
  const name = named && group.sentence.includes(named.title) ? named.title : named?.name ?? null;
  return (
    <li key={item.id} data-fit="" className={cn("lact-ev", dense && "lact-dense", group.fresh && motionAllowed() && "live-stream-new")} data-glow={newest ? "" : undefined}>
      <Link to={entityLink(item, titles)} title={many > 1 ? group.detail.join("\n") : undefined}>
        <span aria-hidden className={cn("lact-ic", kind.tile, kind.text)}>
          <kind.icon />
        </span>
        {dense ? (
          <>
            <span title={`${group.sentence} (${eventLabel(item)})`} className="lact-w">{boldTitle(group.sentence, name)}</span>
            <span className="when">{many > 1 ? `${many.toLocaleString("en-AU")} times · ` : null}{ago(item.createdUtc)}</span>
          </>
        ) : (
          <span className="min-w-0">
            <span title={group.sentence} className="lact-w line-clamp-2 block">{boldTitle(group.sentence, name)}</span>
            <span className="lact-k block">
              <span className={kind.text}>{eventLabel(item)}</span>
              {many > 1 ? <span className={cn("ml-1.5 rounded-full px-1.5 py-px text-(length:--type-micro) font-semibold tabular-nums", kind.tile, kind.text)}>{many.toLocaleString("en-AU")} times</span> : null} &middot; <span className="when">{ago(item.createdUtc)}</span>
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
  // Whole entries only, never one cut through: the rest are counted in the header, and all of them are in Activity.
  const [boxRef, fits] = useFittingRows();
  const more = Math.max(0, shown.length - Math.min(fits, MAX_ROWS));
  const quiet = routine > 0 ? `${routine.toLocaleString("en-AU")} routine left out` : "as it happens";
  // As Weir's: the count line stays what it is, and the entries that do not fit are one link beside "All activity".
  const moreLink = more > 0 ? (
    <Link to={ACTIVITY_HISTORY} className="lact-more" aria-label={`${more.toLocaleString("en-AU")} more in the activity log`}>{more.toLocaleString("en-AU")} more</Link>
  ) : null;
  return (
    <Panel title="Activity" count={quiet} aside={moreLink} to={ACTIVITY_HISTORY} toLabel="All activity" className="lact min-h-40 flex-1">
      {shown.length === 0 ? (
        <Plain>Nothing has happened yet. What Deluno does shows up here as it works.</Plain>
      ) : (
        // The newest on top; as many whole entries as the box holds, and the rest one click away.
        <ul ref={boxRef as unknown as RefObject<HTMLUListElement>} data-testid="live-stream" className="lact-stream">
          {shown.slice(0, MAX_ROWS).map((group, index) => renderRow(group, titles, dense, index === 0))}
        </ul>
      )}
    </Panel>
  );
}

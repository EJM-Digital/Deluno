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
import { RhythmChart } from "./rhythm";
import type { HourCountRow, RhythmRow } from "./series";
import { activityKind, isRoutine, KIND_STYLE } from "./kinds";
import { entityFromRelated, namedTitle, type TitleLookup } from "./titles";

/** What an episode search found, in a few words. */
function outcomeWords(outcome: string): string {
  const said = outcome.trim().toLowerCase();
  if (said === "skipped") return "nothing better than what you have";
  if (/^(queued|grabbed|sent)/.test(said)) return "found a release and sent it to your client";
  if (/no[-\s_]?(results?|match|release)/.test(said)) return "no release found";
  return said.replace(/[-_]+/g, " ");
}

/** The sentence for an event: a search for an episode names the show, and says what it found, instead of repeating the server's bookkeeping. */
export function sentenceOf(item: Pick<LiveActivityRow, "message" | "relatedEntityType" | "relatedEntityId">, titles: TitleLookup): string {
  const search = /^Episode search executed:\s*(S\d{1,2}E\d{1,3})\s*-\s*(.+)$/i.exec(item.message);
  if (!search) return item.message;
  const named = namedTitle(entityFromRelated(item.relatedEntityType, item.relatedEntityId), titles);
  return `Searched ${named ? `${named.name} ${search[1]}` : search[1]} — ${outcomeWords(search[2])}`;
}

function entityLink(item: Pick<LiveActivityRow, "relatedEntityType" | "relatedEntityId">, titles: TitleLookup): string {
  const named = namedTitle(entityFromRelated(item.relatedEntityType, item.relatedEntityId), titles);
  if (named) return named.to;
  const kind = item.relatedEntityType?.toLowerCase() ?? "";
  if (!item.relatedEntityId) return "/activity";
  if (kind.includes("movie")) return `/movies/${item.relatedEntityId}`;
  if (kind.includes("series") || kind.includes("show")) return `/tv/${item.relatedEntityId}`;
  return "/activity";
}

function renderRow(item: LiveActivityRow, titles: TitleLookup): ReactElement {
  const kind = KIND_STYLE[activityKind(item.category, item.severity)];
  return (
    <li key={item.id} className={cn("border-b border-hairline last:border-b-0", item.fresh && motionAllowed() && "live-stream-new")}>
      <Link
        to={entityLink(item, titles)}
        className="flex h-full items-center gap-2.5 px-[var(--card-pad-x)] py-2 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        <span aria-hidden className={cn("mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg", kind.tile, kind.text)}>
          <kind.icon className="h-4 w-4" />
        </span>
        <span className="min-w-0 flex-1">
          <span className="block text-[length:var(--type-body-sm)] leading-snug text-foreground">{sentenceOf(item, titles)}</span>
          <span className="block text-[length:var(--type-caption)] text-muted-foreground">
            <span className={kind.text}>{eventLabel(item)}</span> &middot; {ago(item.createdUtc)}
          </span>
        </span>
      </Link>
    </li>
  );
}

export function ActivityStream({ rows, titles, today }: { rows: LiveActivityRow[]; titles: TitleLookup; today?: { rows: RhythmRow[]; counts?: HourCountRow[] | null; countedAt?: number; capped?: boolean } }) {
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
    <Panel title="Activity" count="what Deluno did, as it does it" to="/activity" toLabel="All activity">
      <RhythmChart rows={today?.rows ?? rows} counts={today?.counts} countedAt={today?.countedAt} capped={today?.capped} />
      {shown.length === 0 ? (
        <Plain>Nothing has happened yet. What Deluno does shows up here as it works.</Plain>
      ) : (
        <FitList moreTo="/activity" fill rows={shown.map((row) => renderRow(row, titles))} />
      )}
      {routine > 0 ? (
        <Link to="/activity" className="shrink-0 border-t border-hairline px-[var(--card-pad-x)] py-1.5 text-[length:var(--type-caption)] text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
          and {routine.toLocaleString("en-AU")} routine background {routine === 1 ? "event" : "events"}, left out
        </Link>
      ) : null}
    </Panel>
  );
}

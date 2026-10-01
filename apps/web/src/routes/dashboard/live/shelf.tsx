/**
 * Arrived today (#1098): the posters of what landed in the library today, newest first,
 * with the quality it came in at and, for an upgrade, the quality it replaced. The
 * header says how many titles, how many were upgrades and how much space they added.
 * Posters keep their 2:3 shape and size themselves to the shelf: one row on a short
 * shelf, more rows on a tall screen, and as many as fit across. The newest one has
 * a green ring for a few seconds; one that has just been imported flies in from the
 * journey (see `useFlyToShelf`) and simply appears with reduced motion.
 */
import { useMemo } from "react";
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import { cn } from "../../../lib/utils";
import { formatSize } from "../downloads-kit";
import { ago } from "../format";
import { LiveCard as Panel } from "./live-card";
import { initialsOf } from "./card";
import type { ArrivalItem } from "./extras";
import { useElementSize, useRemPx } from "./hooks";
import { posterLayout } from "./layout";
import { groupArrivals, type ArrivalGroup } from "./arrival-groups";
import { LANES } from "./lanes";
import type { TitleLookup } from "./titles";

/** The header when nothing has come in today and the shelf shows the latest of the last seven days. */
export const LATEST_ARRIVALS_HEADING = "Nothing yet today · latest arrivals";

/** What the shelf calls when a title arrived: "12 min ago" today, then "yesterday", "3 days ago" by calendar day. */
export function arrivedWhen(at: string, now = Date.now()): string {
  const then = new Date(at);
  if (Number.isNaN(then.getTime())) return ago(at, now);
  const day = (date: Date) => Date.UTC(date.getFullYear(), date.getMonth(), date.getDate());
  const days = Math.round((day(new Date(now)) - day(then)) / 86_400_000);
  if (days <= 0) return ago(at, now);
  return days === 1 ? "yesterday" : `${days} days ago`;
}

/**
 * The size of a poster, worked out by the browser from the box it sits in (a container query unit), so a poster can never
 * be taller than its row whatever the timing of a measurement: the row is a share of the box, the poster the row less its
 * caption, 2:3. JavaScript only decides how many rows and how many posters to draw.
 */
export const POSTER_WIDTH_CSS = "min(10rem, max(2.5rem, calc((((100cqh - (var(--poster-rows) - 1) * var(--poster-gap)) / var(--poster-rows)) - var(--poster-caption)) * 2 / 3)))";

/** "12 titles · 4 upgraded · 54 GB added": what today has brought in. */
export function arrivedHeading(titles: number, upgraded: number, bytes: number): string {
  const parts = [`${titles.toLocaleString("en-AU")} ${titles === 1 ? "title" : "titles"}`];
  if (upgraded > 0) parts.push(`${upgraded.toLocaleString("en-AU")} upgraded`);
  if (bytes > 0) parts.push(`${formatSize(bytes).replace(/.0 /, " ")} added`);
  return parts.join(" · ");
}

function Poster({ group, isNew }: { group: ArrivalGroup; isNew: boolean }) {
  const { title, poster, to, tag, upgrades, episodes } = group;
  const when = arrivedWhen(group.at);
  const sub = episodes ? `${episodes} · ${when}` : when;
  const tip = [title, episodes, tag, group.sizeBytes ? formatSize(group.sizeBytes) : null, upgrades > 0 ? (group.items.length > 1 ? `${upgrades} upgraded` : "an upgrade") : null, group.items.length === 1 && group.items[0].requestedBy ? `requested by ${group.items[0].requestedBy}` : null, when].filter(Boolean).join(" · ");
  return (
    <li className="flex shrink-0 flex-col" style={{ width: "var(--poster-w)", height: "calc(var(--poster-w) * 1.5 + var(--poster-caption))" }}>
      <Link to={to} title={tip} aria-label={`${title}${episodes ? ` ${episodes}` : ""}: in the library, ${when}`} className="group flex min-h-0 flex-1 flex-col rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
        <span data-arrival-poster={group.key} data-arrival-members={group.items.map((item) => item.key).join("|")} className={cn("relative block min-h-0 flex-1 overflow-hidden rounded-[10px] bg-surface-3 shadow-[0_8px_24px_hsl(0_0%_0%/0.35)] transition-shadow duration-500", isNew && "shadow-[0_0_0_2px_hsl(var(--success)),0_0_26px_hsl(var(--success)/0.45)]")}>
          <ArtworkImage
            src={poster}
            alt=""
            loading="lazy"
            className="h-full w-full object-cover"
            fallback={
              <span aria-hidden className="flex h-full w-full flex-col items-center justify-center gap-0.5 bg-surface-3">
                <LANES.imported.icon className="h-4 w-4 text-success" />
                <span className="text-[length:var(--type-caption)] font-semibold leading-none text-muted-foreground">{initialsOf(title)}</span>
              </span>
            }
          />
          {tag ? <span aria-hidden className={cn("absolute inset-x-1.5 bottom-1.5 truncate rounded-md bg-black/75 px-1.5 py-0.5 text-[length:var(--type-micro)] font-semibold text-white backdrop-blur-sm", upgrades > 0 && "text-success")}>{tag}</span> : null}
        </span>
        <span className="mt-1.5 block truncate text-[length:var(--type-caption)] font-semibold leading-tight text-foreground">{title}</span>
        <span className="block truncate text-[length:var(--type-micro)] leading-tight text-muted-foreground">{sub}</span>
      </Link>
    </li>
  );
}

export function Shelf({ items, titles, newest, heading }: { items: ArrivalItem[]; titles: TitleLookup; /** Keys that have just arrived: the newest has the ring. */ newest: ReadonlySet<string>; heading: string }) {
  const [boxRef, box] = useElementSize<HTMLDivElement>();
  const rem = useRemPx();
  // The box is measured inside its padding, so the posters across are worked out from the real space, not an assumed padding.
  const layout = posterLayout(box.width, box.height, rem);
  // Until the shelf has been measured every arrival is drawn.
  const groups = useMemo(() => groupArrivals(items, titles), [items, titles]);
  const shown = box.width > 0 ? groups.slice(0, layout.capacity) : groups;
  return (
    <Panel title="Arrived today" count={heading} to="/movies" toLabel="Your library" className="min-h-[12rem]">
      {/* The measured box is always drawn (never swapped in later), so it is observed from the first render and the posters follow every size change. */}
      <div className="flex min-h-0 flex-1 flex-col px-[var(--card-pad-x)] py-1.5">
        <div ref={boxRef} className="min-h-0 flex-1 overflow-hidden [container-type:size]">
          {items.length === 0 ? (
            <p className="py-1.5 text-[length:var(--type-body-sm)] text-muted-foreground">Nothing has arrived yet today.</p>
          ) : (
            <ul
              className="flex flex-wrap content-start gap-3.5"
              style={{ ["--poster-rows" as string]: layout.rows, ["--poster-gap" as string]: "0.875rem", ["--poster-caption" as string]: "2.25rem", ["--poster-w" as string]: POSTER_WIDTH_CSS }}
            >
              {shown.map((group) => <Poster key={group.key} group={group} isNew={group.items.some((item) => newest.has(item.key))} />)}
            </ul>
          )}
        </div>
      </div>
    </Panel>
  );
}

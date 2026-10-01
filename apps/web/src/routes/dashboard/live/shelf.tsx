/**
 * Arrived today (#1098): the posters of what landed in the library today, newest first,
 * with the quality it came in at and, for an upgrade, the quality it replaced. The
 * header says how many titles, how many were upgrades and how much space they added.
 * Posters keep their 2:3 shape and fill the shelf: one row sized from the shelf's height (at most 130px wide): more across rather than bigger, never a partial second row. The newest one has
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
import "./shelf.css";
import { initialsOf } from "./card";
import type { ArrivalItem } from "./extras";
import { useElementSize } from "./hooks";
import { MAX_POSTER_PX, MIN_POSTER_PX, POSTER_CAPTION_PX, POSTER_GAP_PX, posterLayout } from "./layout";
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

/** The size of a poster from the box it sits in (a container query unit): one row, 2:3, the box less its caption, between 40px and 130px, so it can never be taller than the shelf whatever the timing of a measurement. */
export const POSTER_WIDTH_CSS = `min(${MAX_POSTER_PX}px, max(${MIN_POSTER_PX}px, calc((100cqh - var(--poster-caption)) * 2 / 3)))`;

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
    <li className="lsh-poster" data-new={isNew ? "" : undefined} style={{ width: "var(--poster-w)", height: "calc(var(--poster-w) * 1.5 + var(--poster-caption))" }}>
      <Link to={to} title={tip} aria-label={`${title}${episodes ? ` ${episodes}` : ""}: in the library, ${when}`}>
        <span data-arrival-poster={group.key} data-arrival-members={group.items.map((item) => item.key).join("|")} className="lsh-art">
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
          {tag ? <span aria-hidden className={upgrades > 0 ? "lsh-tag up" : "lsh-tag"}>{tag}</span> : null}
        </span>
        <span className="lsh-cap">{title}<small>{sub}</small></span>
      </Link>
    </li>
  );
}

export function Shelf({ items, titles, newest, heading }: { items: ArrivalItem[]; titles: TitleLookup; /** Keys that have just arrived: the newest has the ring. */ newest: ReadonlySet<string>; heading: string }) {
  const [boxRef, box] = useElementSize<HTMLDivElement>();
  // The box is measured inside its padding, so the posters across are worked out from the real space, not an assumed padding.
  // Until the shelf has been measured every arrival is drawn.
  const groups = useMemo(() => groupArrivals(items, titles), [items, titles]);
  const layout = posterLayout(box.width, box.height);
  const shown = box.width > 0 ? groups.slice(0, layout.capacity) : groups;
  return (
    <Panel title="Arrived today" count={heading} to="/movies" toLabel="Your library" className="lsh min-h-[11rem]">
      {/* The measured box is always drawn (never swapped in later), so it is observed from the first render and the posters follow every size change. */}
      <div className="flex min-h-0 flex-1 flex-col px-[var(--card-pad-x)] py-1.5">
        <div ref={boxRef} className="min-h-0 flex-1 overflow-hidden [container-type:size]">
          {items.length === 0 ? (
            <p className="lsh-empty">Nothing has arrived yet today.</p>
          ) : (
            <ul
              className="lsh-posters"
              style={{ ["--poster-gap" as string]: `${POSTER_GAP_PX}px`, ["--poster-caption" as string]: `${POSTER_CAPTION_PX}px`, ["--poster-w" as string]: POSTER_WIDTH_CSS }}
            >
              {shown.map((group) => <Poster key={group.key} group={group} isNew={group.items.some((item) => newest.has(item.key))} />)}
            </ul>
          )}
        </div>
      </div>
    </Panel>
  );
}

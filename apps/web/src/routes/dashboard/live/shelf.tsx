/**
 * Your library (#1098, #1103): what landed today on one shelf, newest first. Under the card's header a row of chips picks
 * the library the shelf shows ("All" and then each library by name, in Settings › Libraries order). There is no legend:
 * the colours on the posters are the Movies page's own (the owner, 1 Oct 2026). Each poster has the Movies page's bar across its top (gold for
 * quality met, green for upgradable, the quality on it), a small arrow when it replaced a worse copy, and a tag with the
 * name of the library it landed in, which opens that library's page filtered to it.
 *
 * Posters are sized from the row's own height, exactly 2:3, and only whole posters are drawn. A title that has just been
 * imported flies in from the journey (see `useFlyToShelf`) when the chip shown includes its library, and simply appears
 * with reduced motion. With nothing in today, the shelf shows the latest of the last seven days.
 */
import { useLayoutEffect, useMemo, useState, type CSSProperties, type MouseEvent, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import type { DashboardLibrarySection } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { formatSize } from "../downloads-kit";
import { ago } from "../format";
import { LiveCard as Panel } from "./live-card";
import "./shelf.css";
import { PosterTile } from "./card";
import type { ArrivalItem } from "./extras";
import { useElementSize } from "./hooks";
import { posterDetail, posterStatus } from "./poster-caption";
import { libraryPosters, posterSize, postersAcross, shelfRowNeed, type CaptionTier } from "./layout";
import { groupArrivals, type ArrivalGroup } from "./arrival-groups";
import { useOpenSlots } from "./shelf-slots";
import { TILE_KEY_ATTRIBUTE, useSlideNeighbours, useTileArrivals } from "./shelf-motion";
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

/** The header: "12 arrived today". What it adds up to is in its tooltip (see `arrivedDetail`). */
export function arrivedHeading(titles: number): string {
  return `${titles.toLocaleString("en-AU")} arrived today`;
}

/** "12 titles · 4 upgraded · 54 GB added": what today has brought in. */
export function arrivedDetail(titles: number, upgraded: number, bytes: number): string {
  const parts = [`${titles.toLocaleString("en-AU")} ${titles === 1 ? "title" : "titles"}`];
  if (upgraded > 0) parts.push(`${upgraded.toLocaleString("en-AU")} upgraded`);
  if (bytes > 0) parts.push(`${formatSize(bytes).replace(/.0 /, " ")} added`);
  return parts.join(" · ");
}

/**
 * A library's colour on the strip a small poster wears: hues from outside the status colours (green, amber, red), taken in
 * Settings › Libraries order and then round again. Weir uses its workflow hues the same way.
 */
export const LIBRARY_HUES = [205, 265, 320, 188, 232, 290] as const;
export const libraryHue = (position: number): number => LIBRARY_HUES[Math.max(0, position) % LIBRARY_HUES.length];

/** The chosen chip is remembered per browser. */
export const SHELF_LIBRARY_KEY = "deluno.live.shelfLibrary";

function readChoice(): string {
  try {
    return localStorage.getItem(SHELF_LIBRARY_KEY) || "all";
  } catch {
    return "all";
  }
}

function rememberChoice(value: string): void {
  try {
    if (value === "all") localStorage.removeItem(SHELF_LIBRARY_KEY);
    else localStorage.setItem(SHELF_LIBRARY_KEY, value);
  } catch {
    /* the choice just is not kept */
  }
}

function Poster({ group, isNew, caption, tag, small }: { group: ArrivalGroup; isNew: boolean; caption: CaptionTier; tag: { id: string; name: string; hue: number } | null; small: boolean }) {
  const { title, poster, to, upgrades, episodes } = group;
  const when = arrivedWhen(group.at);
  const status = posterStatus(group);
  const detailLine = posterDetail(group);
  const state = group.met === true ? "quality met" : group.met === false ? "can still be upgraded" : null;
  // The upgrade is said in the status line; the tooltip and the name say it in full.
  const upgradeSaid = upgrades > 0 ? (group.items.length > 1 ? `${upgrades} upgraded` : group.previousQuality ? `upgraded from ${group.previousQuality}` : "an upgrade") : null;
  const tip = [title, episodes, group.tag, state, group.sizeBytes ? formatSize(group.sizeBytes) : null, upgradeSaid, group.items.length === 1 && group.items[0].requestedBy ? `requested by ${group.items[0].requestedBy}` : null, when].filter(Boolean).join(" · ");
  const page = group.library === "tv" ? "/tv" : "/movies";
  return (
    <li className="lsh-poster" {...{ [TILE_KEY_ATTRIBUTE]: group.key }} data-size={small ? "small" : "full"} data-caption={caption} data-new={isNew ? "" : undefined} data-tagged={tag ? "" : undefined}>
      <Link to={to} title={tip} aria-label={`${title}${episodes ? ` ${episodes}` : ""}: in the library${upgradeSaid ? `, ${upgradeSaid}` : ""}, ${when}`}>
        <span data-arrival-poster={group.key} data-arrival-members={group.items.map((item) => item.key).join("|")} className="lsh-art">
          <ArtworkImage
            src={poster}
            alt=""
            loading="lazy"
            className="h-full w-full object-cover"
            fallback={<PosterTile title={title} kind={group.library} hue={tag?.hue} />}
          />
        </span>
        {caption === "none" ? null : (
          <span className={cn("lsh-cap", caption === "full" && "lsh-cap--full")}>
            <span className="lsh-title">{title}</span>
            <span className="lsh-status" data-tone={status.tone}>
              <i aria-hidden className="lsh-dot" />
              <span>{status.words}</span>
            </span>
            {caption === "full" ? (
              <>
                <small className="lsh-detail">{detailLine}</small>
                <small className="lsh-when">{when}</small>
              </>
            ) : null}
          </span>
        )}
      </Link>
      {tag ? (
        <Link
          className="lsh-tag"
          to={`${page}?library=${encodeURIComponent(tag.id)}`}
          title={`Open ${tag.name} in the library`}
          style={{ ["--tile-hue" as string]: tag.hue }}
          onClick={(event: MouseEvent) => event.stopPropagation()}
        >
          {tag.name}
        </Link>
      ) : null}
    </li>
  );
}

export function Shelf({ items, titles, newest, heading, detail, latest = false, library, onHeightNeed }: {
  items: ArrivalItem[];
  titles: TitleLookup;
  /** Keys that have just arrived: the newest has the ring. */
  newest: ReadonlySet<string>;
  /** The header's quiet line. */
  heading: string;
  /** What the heading adds up to, said on hover. */
  detail?: string;
  /** The items are the latest of the last seven days, not today's. */
  latest?: boolean;
  /** The library's own counts: for each kind of title, and for each library. */
  library?: DashboardLibrarySection | null;
  /**
   * Told the height the card can use (its own chrome and the posters at the width the five-poster rule caps them to), so the page
   * can give spare height to something else; see `gridRows`. It does not change with the card's height, so telling it cannot loop.
   */
  onHeightNeed?: (px: number) => void;
}) {
  const groups = useMemo(() => groupArrivals(items, titles), [items, titles]);
  const libraries = useMemo(() => library?.libraryCounts ?? [], [library?.libraryCounts]);
  const [stored, setStored] = useState(readChoice);
  // A library that is gone is All again.
  const chosen = libraries.find((entry) => entry.libraryId === stored) ?? null;
  const choose = (value: string) => {
    setStored(value);
    rememberChoice(value);
  };
  const names = useMemo(() => new Map(libraries.map((entry) => [entry.libraryId, entry.libraryName])), [libraries]);
  const shown = chosen ? groups.filter((group) => group.libraryId === chosen.libraryId) : groups;

  const [boxRef, box] = useElementSize<HTMLUListElement>();
  // The poster size comes from the row's own measured box, so nothing shifts after paint.
  const posters = box.height > 0 ? libraryPosters(box.height, box.width) : null;
  const across = posters ? postersAcross(box.width, posters.width) : 0;
  const rowStyle = posters ? ({ ["--aw" as string]: `${posters.width}px` } satisfies CSSProperties) : undefined;
  const small = posterSize(posters?.width) === "small";
  // The card's chrome (header, chips, padding) is what its height has beyond the row's, and does not change with the height:
  // the need is that and the height the row's posters can use at this width.
  useLayoutEffect(() => {
    const row = boxRef.current;
    if (!row || !onHeightNeed || !(row.clientHeight > 0)) return;
    const card = row.closest<HTMLElement>(".lsh");
    if (card) onHeightNeed(card.offsetHeight - row.clientHeight + shelfRowNeed(row.clientWidth));
  }, [box.width, box.height, onHeightNeed, boxRef]);
  // A title whose poster is on its way has a slot that is closed or opening: the poster it pushes out is kept until it is gone.
  const slots = useOpenSlots(boxRef);
  const inRow = posters ? shown.slice(0, across + slots) : shown;
  useSlideNeighbours(boxRef, inRow.map((group) => group.key).join("|"), posters?.width ?? null);
  useTileArrivals(boxRef, shown.map((group) => group.key).join("|"));
  const headingNode: ReactNode = <span title={detail}>{heading}</span>;

  return (
    <Panel title="Your library" count={headingNode} to="/movies" toLabel="Library" className="lsh min-h-[11rem]">
      {libraries.length > 0 ? (
        <div className="lsh-chips" role="group" aria-label="Libraries">
          <button type="button" className="lsh-chip" aria-pressed={!chosen} data-library="all" onClick={() => choose("all")}>All</button>
          {libraries.map((entry) => (
            <button key={entry.libraryId} type="button" className="lsh-chip" aria-pressed={chosen?.libraryId === entry.libraryId} data-library={entry.libraryId} onClick={() => choose(entry.libraryId)}>{entry.libraryName}</button>
          ))}
        </div>
      ) : null}
      <ul ref={boxRef} className="lsh-posters" style={rowStyle}>
        {inRow.length === 0 ? <li className="lsh-empty">{latest ? "Nothing in the last seven days." : "Nothing has arrived yet today."}</li> : inRow.map((group) => {
          const id = group.libraryId;
          const name = id ? names.get(id) ?? group.libraryName : null;
          const position = libraries.findIndex((entry) => entry.libraryId === id);
          return <Poster key={group.key} group={group} isNew={group.items.some((item) => newest.has(item.key))} caption={posters?.caption ?? "compact"} tag={id && name ? { id, name, hue: libraryHue(position) } : null} small={small} />;
        })}
      </ul>
    </Panel>
  );
}

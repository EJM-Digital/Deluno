/**
 * Your library (#1098, #1103): what landed today on one shelf, newest first. In the card's header, where its count was, a row of
 * chips picks the library the shelf shows ("All" and then each library by name, in Settings › Libraries order); they scroll
 * sideways when the header is narrow, and the count stays as the card's accessible description (#1112, as Weir's Just finished). There is no legend:
 * the colours on the posters are the Movies page's own (James, 1 Oct 2026). Each poster has the Movies page's bar across its top (gold for
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
import { FilterChips } from "../../../components/ui/filter-chips";
import type { DashboardLibrarySection } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { formatBytes } from "../../../lib/utils";
import { ago } from "../format";
import { LiveCard as Panel } from "./live-card";
import "./shelf.css";
import { PosterTile } from "./card";
import { canvasMeasure, lineWords, statusFont, STATUS_MARK_PX, type MeasureText } from "./caption-fit";
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
  if (bytes > 0) parts.push(`${formatBytes(bytes).replace(/.0 /, " ")} added`);
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

/** Where text cannot be measured, as in a test, the fullest short words are taken to fit. */
const FITS_ANYTHING: MeasureText = () => 0;

function Poster({ group, isNew, caption, tag, small, width, measure }: { group: ArrivalGroup; isNew: boolean; caption: CaptionTier; tag: { id: string; name: string; hue: number } | null; small: boolean; width: number | null; measure: MeasureText | null }) {
  const { title, poster, to, upgrades, episodes } = group;
  const when = arrivedWhen(group.at);
  const status = posterStatus(group);
  // The tiny caption is the status line alone: the shortest of its words that fits under the poster, else the dot alone.
  const statusWords = caption !== "tiny" ? status.words : width === null ? status.short[0] : lineWords(status.short, width - STATUS_MARK_PX, measure ?? FITS_ANYTHING);
  const detailLine = posterDetail(group);
  const state = group.met === true ? "quality met" : group.met === false ? "can still be upgraded" : null;
  // The upgrade is said in the status line; the tooltip and the name say it in full.
  const upgradeSaid = upgrades > 0 ? (group.items.length > 1 ? `${upgrades} upgraded` : group.previousQuality ? `upgraded from ${group.previousQuality}` : "an upgrade") : null;
  const tip = [title, episodes, group.tag, state, group.sizeBytes ? formatBytes(group.sizeBytes) : null, upgradeSaid, group.items.length === 1 && group.items[0].requestedBy ? `requested by ${group.items[0].requestedBy}` : null, when].filter(Boolean).join(" · ");
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
          <span className={cn("lsh-cap", caption === "full" && "lsh-cap--full")} data-tier={caption}>
            {caption === "tiny" ? null : <span className="lsh-title">{title}</span>}
            <span className="lsh-status" data-tone={status.tone}>
              <i aria-hidden className="lsh-dot" />
              <span>{statusWords}</span>
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
  // The chips live in the card's header (FilterChips, the one filter control, in its card-header size) and scroll sideways there.
  const chips: ReactNode = libraries.length > 0 ? (
    <FilterChips
      label="Libraries"
      size="sm"
      value={chosen?.libraryId ?? "all"}
      onValueChange={choose}
      options={[{ value: "all", label: "All" }, ...libraries.map((entry) => ({ value: entry.libraryId, label: entry.libraryName }))]}
    />
  ) : null;
  const headingNode: ReactNode = <span title={detail}>{heading}</span>;
  // The status line is measured in its own font, read before paint and again when the fonts arrive.
  const [measured, setMeasured] = useState<MeasureText | null>(null);
  useLayoutEffect(() => {
    const host = boxRef.current;
    if (!host) return undefined;
    let live = true;
    const read = () => {
      if (live) setMeasured(() => canvasMeasure(statusFont(host)));
    };
    read();
    void document.fonts?.ready.then(read);
    return () => {
      live = false;
    };
  }, []);

  return (
    <Panel title="Your library" count={headingNode} controls={chips} description={chips ? (detail ? `${heading}: ${detail}` : heading) : undefined} to="/movies" toLabel="Library" className="lsh">
      <ul ref={boxRef} className="lsh-posters" style={rowStyle}>
        {inRow.length === 0 ? <li className="lsh-empty">{latest ? "Nothing in the last seven days." : "Nothing has arrived yet today."}</li> : inRow.map((group) => {
          const id = group.libraryId;
          const name = id ? names.get(id) ?? group.libraryName : null;
          const position = libraries.findIndex((entry) => entry.libraryId === id);
          return <Poster key={group.key} group={group} isNew={group.items.some((item) => newest.has(item.key))} caption={posters?.caption ?? "compact"} tag={id && name ? { id, name, hue: libraryHue(position) } : null} small={small} width={posters?.width ?? null} measure={measured} />;
        })}
      </ul>
    </Panel>
  );
}

/**
 * Your library (#1098, the approved mockup like for like): what landed today, in the library's own two rows. TV Shows sit
 * above Movies, each with its label, its counts in the Movies page's colours (quality met, upgradable, downloading,
 * missing) and "+N today"; beside them the posters of today's arrivals, newest first, each with the Movies page's bar across its top
 * (gold for quality met, green for upgradable, the quality on it) and a small arrow when it replaced a worse copy.
 *
 * Posters are sized from each row's own height, exactly 2:3, the same size in both rows, and only whole posters are drawn.
 * When stacked rows would make them under 80px tall the two rows go side by side instead (see `libraryBySide`). A title that
 * has just been imported flies in from the journey (see `useFlyToShelf`) and simply appears with reduced motion. With nothing
 * in today, the rows show the latest of the last seven days.
 */
import { useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from "react";
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import type { DashboardLibrarySection, DashboardMediaCounts } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { formatSize } from "../downloads-kit";
import { ago } from "../format";
import { LiveCard as Panel } from "./live-card";
import "./shelf.css";
import { initialsOf } from "./card";
import type { ArrivalItem } from "./extras";
import { useElementSize } from "./hooks";
import { libraryBySide, libraryPosters, postersAcross, type LibraryPosters } from "./layout";
import { groupArrivals, type ArrivalGroup, type ArrivalLibrary } from "./arrival-groups";
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

const ROWS: ReadonlyArray<{ key: ArrivalLibrary; label: string; counts: "shows" | "movies" }> = [
  { key: "tv", label: "TV Shows", counts: "shows" },
  { key: "movies", label: "Movies", counts: "movies" }
];

/** The library's own counts, in the colours of the Movies page. */
function Counts({ counts }: { counts: DashboardMediaCounts | undefined }) {
  if (!counts) return <span className="lsh-ct" />;
  const entries: Array<[string, string, number, string]> = [
    ["Quality met", "met", counts.qualityMet, "hsl(var(--mark-quality-met))"],
    ["Upgradable", "upgradable", counts.upgradable, "hsl(var(--mark-upgrade))"],
    ["Downloading", "downloading", counts.downloading, "hsl(var(--mark-downloading))"],
    ["Missing", "missing", counts.missing, "hsl(var(--mark-missing))"]
  ];
  return (
    <span className="lsh-ct">
      {entries.map(([title, word, value, colour]) => (
        <span key={word} title={title}><i style={{ background: colour }} /><b>{value.toLocaleString("en-AU")}</b><span className="lsh-w"> {word}</span></span>
      ))}
    </span>
  );
}

function Poster({ group, isNew, captions }: { group: ArrivalGroup; isNew: boolean; captions: boolean }) {
  const { title, poster, to, tag, upgrades, episodes } = group;
  const when = arrivedWhen(group.at);
  const sub = episodes ? `${episodes} · ${when}` : when;
  const state = group.met === true ? "quality met" : group.met === false ? "can still be upgraded" : null;
  const tip = [title, episodes, tag, state, group.sizeBytes ? formatSize(group.sizeBytes) : null, upgrades > 0 ? (group.items.length > 1 ? `${upgrades} upgraded` : "an upgrade") : null, group.items.length === 1 && group.items[0].requestedBy ? `requested by ${group.items[0].requestedBy}` : null, when].filter(Boolean).join(" · ");
  return (
    <li className="lsh-poster" data-new={isNew ? "" : undefined}>
      <Link to={to} title={tip} aria-label={`${title}${episodes ? ` ${episodes}` : ""}: in the library, ${when}`}>
        <span data-arrival-poster={group.key} data-arrival-members={group.items.map((item) => item.key).join("|")} className="lsh-art">
          {group.quality ? <span aria-hidden className={cn("lsh-mark", group.met === true && "met", group.met === false && "upg")}>{group.quality}</span> : null}
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
          {upgrades > 0 ? <span className="lsh-up" title={group.previousQuality ? `Upgraded from ${group.previousQuality}` : "Upgraded"}>↑</span> : null}
        </span>
        {captions ? <span className="lsh-cap">{title}<small>{sub}</small></span> : null}
      </Link>
    </li>
  );
}

interface Fit {
  posters: LibraryPosters;
  across: Record<ArrivalLibrary, number>;
}

export function Shelf({ items, titles, newest, heading, detail, latest = false, library }: {
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
  /** The library's own counts, for each row. */
  library?: DashboardLibrarySection | null;
}) {
  const groups = useMemo(() => groupArrivals(items, titles), [items, titles]);
  const [libsRef, libs] = useElementSize<HTMLDivElement>();
  const boxes: Record<ArrivalLibrary, RefObject<HTMLUListElement | null>> = { tv: useRef<HTMLUListElement>(null), movies: useRef<HTMLUListElement>(null) };
  const byside = libraryBySide(libs.height);
  const [fit, setFit] = useState<Fit | null>(null);
  // The poster size comes from the rows' own measured boxes (after the stacked or side-by-side choice is drawn), before paint, so nothing shifts.
  useLayoutEffect(() => {
    const tv = boxes.tv.current;
    const movies = boxes.movies.current;
    if (!tv || !movies || !(tv.clientHeight > 0) || !(movies.clientHeight > 0)) return;
    const posters = libraryPosters([tv.clientHeight, movies.clientHeight]);
    const next: Fit = { posters, across: { tv: postersAcross(tv.clientWidth, posters.width), movies: postersAcross(movies.clientWidth, posters.width) } };
    setFit((current) => (current && current.posters.width === next.posters.width && current.posters.captions === next.posters.captions && current.posters.short === next.posters.short && current.across.tv === next.across.tv && current.across.movies === next.across.movies ? current : next));
  }, [libs.width, libs.height, byside, boxes.tv, boxes.movies]);
  const rowStyle = fit ? ({ ["--aw" as string]: `${fit.posters.width}px` } satisfies CSSProperties) : undefined;
  const headingNode: ReactNode = <span title={detail}>{heading}</span>;
  return (
    <Panel title="Your library" count={headingNode} to="/movies" toLabel="Library" className="lsh min-h-[11rem]">
      <div ref={libsRef} className={cn("lsh-libs", byside && "byside", fit?.posters.short && "short")}>
        {ROWS.map((row) => {
          const inRow = groups.filter((group) => group.library === row.key);
          const shown = fit ? inRow.slice(0, fit.across[row.key]) : inRow;
          return (
            <section key={row.key} className="lsh-lib" aria-label={row.label}>
              <div className="lsh-lib-h">
                <span className="lsh-label">{row.label}</span>
                <Counts counts={library?.[row.counts]} />
                <span className="lsh-today">{latest ? "latest" : `+${inRow.length.toLocaleString("en-AU")} today`}</span>
              </div>
              <ul ref={boxes[row.key]} className="lsh-posters" style={rowStyle}>
                {shown.length === 0 ? <li className="lsh-empty">{latest ? "Nothing in the last seven days." : "Nothing has arrived yet today."}</li> : shown.map((group) => <Poster key={group.key} group={group} isNew={group.items.some((item) => newest.has(item.key))} captions={fit?.posters.captions ?? true} />)}
              </ul>
            </section>
          );
        })}
      </div>
    </Panel>
  );
}

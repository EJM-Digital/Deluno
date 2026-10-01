/**
 * Arrived today (#1098): the posters of what landed in the library today, newest first,
 * with the quality it came in at and, for an upgrade, the quality it replaced. The
 * header says how many titles, how many were upgrades and how much space they added.
 * Posters keep their 2:3 shape and size themselves to the shelf: one row on a short
 * shelf, more rows on a tall screen, and as many as fit across. The newest one has
 * a green ring for a few seconds; one that has just been imported flies in from the
 * journey (see `useFlyToShelf`) and simply appears with reduced motion.
 */
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
import { nameOf } from "./arrivals";
import { LANES } from "./lanes";
import type { TitleLookup } from "./titles";

/** "12 titles · 4 upgraded · 54 GB added": what today has brought in. */
export function arrivedHeading(titles: number, upgraded: number, bytes: number): string {
  const parts = [`${titles.toLocaleString("en-AU")} ${titles === 1 ? "title" : "titles"}`];
  if (upgraded > 0) parts.push(`${upgraded.toLocaleString("en-AU")} upgraded`);
  if (bytes > 0) parts.push(`${formatSize(bytes).replace(/.0 /, " ")} added`);
  return parts.join(" · ");
}

function Poster({ item, titles, isNew, width, height }: { item: ArrivalItem; titles: TitleLookup; isNew: boolean; width: number; /** The height of the poster with its caption. */ height: number }) {
  const { title, poster, to } = nameOf(item, titles);
  const tag = item.upgraded && item.previousQuality && item.quality ? `${item.previousQuality} → ${item.quality}` : item.quality;
  const tip = [title, tag, item.sizeBytes ? formatSize(item.sizeBytes) : null, item.upgraded ? "an upgrade" : null, item.requestedBy ? `requested by ${item.requestedBy}` : null, ago(item.at)].filter(Boolean).join(" · ");
  return (
    <li className="flex shrink-0 flex-col" style={{ width, height }}>
      <Link to={to} title={tip} aria-label={`${title}: in the library, ${ago(item.at)}`} className="group flex min-h-0 flex-1 flex-col rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
        <span data-arrival-poster={item.key} className={cn("relative block min-h-0 flex-1 overflow-hidden rounded-[10px] bg-surface-3 shadow-[0_8px_24px_hsl(0_0%_0%/0.35)] transition-shadow duration-500", isNew && "shadow-[0_0_0_2px_hsl(var(--success)),0_0_26px_hsl(var(--success)/0.45)]")}>
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
          {tag ? <span aria-hidden className={cn("absolute inset-x-1.5 bottom-1.5 truncate rounded-md bg-black/75 px-1.5 py-0.5 text-[length:var(--type-micro)] font-semibold text-white backdrop-blur-sm", item.upgraded && "text-success")}>{tag}</span> : null}
        </span>
        <span className="mt-1.5 block truncate text-[length:var(--type-caption)] font-semibold leading-tight text-foreground">{title}</span>
        <span className="block truncate text-[length:var(--type-micro)] leading-tight text-muted-foreground">{ago(item.at)}</span>
      </Link>
    </li>
  );
}

export function Shelf({ items, titles, newest, heading }: { items: ArrivalItem[]; titles: TitleLookup; /** Keys that have just arrived: the newest has the ring. */ newest: ReadonlySet<string>; heading: string }) {
  const [boxRef, box] = useElementSize<HTMLDivElement>();
  const rem = useRemPx();
  const layout = posterLayout(Math.max(0, box.width - 2 * 1.125 * rem), Math.max(0, box.height - 2 * 0.375 * rem), rem);
  // Until the shelf has been measured every arrival is drawn.
  const shown = box.width > 0 ? items.slice(0, layout.capacity) : items;
  return (
    <Panel title="Arrived today" count={heading} to="/movies" toLabel="Your library" className="min-h-[12rem]">
      {items.length === 0 ? (
        <p className="px-[var(--card-pad-x)] py-3 text-[length:var(--type-body-sm)] text-muted-foreground">Nothing has arrived yet today.</p>
      ) : (
        <div ref={boxRef} className="min-h-0 flex-1 overflow-hidden px-[var(--card-pad-x)] py-1.5">
          <ul className="flex flex-wrap content-start gap-3.5">
            {shown.map((item) => <Poster key={item.key} item={item} titles={titles} isNew={newest.has(item.key)} width={layout.width} height={layout.itemHeight} />)}
          </ul>
        </div>
      )}
    </Panel>
  );
}

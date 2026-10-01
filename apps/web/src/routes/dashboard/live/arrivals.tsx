/**
 * The end of the journey (#1096): what has landed in the library today, as a wall
 * of posters that turn over into place as each one arrives, and under it the
 * upgrades won today, each as the quality it had and the quality it has now.
 *
 * Every poster is a real import from today's stored record (a title imported
 * twice is two posters, because it arrived twice); the wall shows whole rows
 * and says how many more there are. A poster with no art is a plain tile with
 * the title's initials, so the wall never has a hole in it.
 */
import { ArrowUpRight, Inbox, Library } from "lucide-react";
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import { motionAllowed } from "../../../lib/page-motion";
import { cn } from "../../../lib/utils";
import { formatSize } from "../downloads-kit";
import { ago } from "../format";
import { FitList } from "../parts";
import { initialsOf } from "./card";
import type { ArrivalItem } from "./extras";
import { LANES } from "./lanes";
import { namedTitle, type TitleLookup } from "./titles";

export function nameOf(item: ArrivalItem, titles: TitleLookup) {
  const named = namedTitle(item.entity, titles, item.release);
  return { title: named?.title ?? item.fallbackTitle, poster: named?.poster, to: named?.to ?? item.to };
}

function Tile({ item, titles, fresh, ...rest }: { item: ArrivalItem; titles: TitleLookup; fresh: boolean; /** Marks a row a FitList measures. */ "data-fit-row"?: string }) {
  const { title, poster, to } = nameOf(item, titles);
  const tip = [title, item.quality, item.sizeBytes ? formatSize(item.sizeBytes) : null, item.upgraded ? "an upgrade" : null, item.requestedBy ? `requested by ${item.requestedBy}` : null, ago(item.at)].filter(Boolean).join(" · ");
  return (
    <li {...rest} className={cn("relative", fresh && motionAllowed() && "live-flip")}>
      <Link to={to} title={tip} aria-label={`${title}: in the library, ${ago(item.at)}`} className="block aspect-[2/3] overflow-hidden rounded-md shadow-sm ring-1 ring-hairline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
        <ArtworkImage
          src={poster}
          alt=""
          loading="lazy"
          className="h-full w-full bg-surface-3 object-cover"
          fallback={
            <span aria-hidden className="flex h-full w-full flex-col items-center justify-center gap-0.5 bg-surface-3">
              <Library className="h-3.5 w-3.5 text-success" />
              <span className="text-[length:var(--type-micro)] font-semibold leading-none text-muted-foreground">{initialsOf(title)}</span>
            </span>
          }
        />
        {item.upgraded ? <span aria-hidden className="absolute bottom-0.5 left-0.5 flex h-4 w-4 items-center justify-center rounded-full bg-success text-white shadow"><ArrowUpRight className="h-3 w-3" /></span> : null}
        {item.requestedBy ? <span aria-hidden className="absolute right-0.5 top-0.5 flex h-4 w-4 items-center justify-center rounded-full bg-primary text-primary-foreground shadow"><Inbox className="h-2.5 w-2.5" /></span> : null}
      </Link>
    </li>
  );
}

/** "WEB 720p → Bluray 1080p" for each upgrade won today. */
function Upgrades({ items, titles }: { items: ArrivalItem[]; titles: TitleLookup }) {
  const won = items.filter((item) => item.upgraded && item.previousQuality && item.quality);
  if (won.length === 0) return null;
  const shown = won.slice(0, 3);
  return (
    <div className="shrink-0 border-t border-hairline px-2 pb-1.5 pt-1.5">
      <p className="flex items-center justify-between text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.06em] text-muted-foreground">
        <span>Upgrades won today</span>
        <span className="tabular-nums text-success">{won.length.toLocaleString("en-AU")}</span>
      </p>
      <ul className="mt-0.5">
        {shown.map((item) => {
          const { title, to } = nameOf(item, titles);
          return (
            <li key={item.key}>
              <Link to={to} aria-label={`${title}: upgraded from ${item.previousQuality} to ${item.quality}`} className="flex items-start gap-1.5 rounded-md py-0.5 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                <ArrowUpRight aria-hidden className="mt-0.5 h-3.5 w-3.5 shrink-0 text-success" />
                <span className="min-w-0 flex-1 leading-tight">
                  <span className="block truncate text-[length:var(--type-caption)] font-medium text-foreground">{title}</span>
                  <span className="block truncate text-[length:var(--type-micro)] text-muted-foreground">{item.previousQuality} <span aria-hidden>→</span> <span className="text-success">{item.quality}</span></span>
                </span>
              </Link>
            </li>
          );
        })}
      </ul>
      {won.length > shown.length ? <Link to="/activity" className="block pt-0.5 text-[length:var(--type-caption)] font-medium text-primary hover:underline">and {(won.length - shown.length).toLocaleString("en-AU")} more</Link> : null}
    </div>
  );
}

/** The body of the "In the library" lane: the poster wall, then the upgrades. */
export function ArrivalsBody({ items, titles, fresh, count, wide = false }: { items: ArrivalItem[]; titles: TitleLookup; fresh: ReadonlySet<string>; count: number; /** Nothing else is moving, so the wall has the whole board: posters keep their size and there are more of them across. */ wide?: boolean }) {
  return (
    <>
      <FitList
        moreTo={LANES.imported.to}
        extra={Math.max(0, count - items.length)}
        columns
        wrap={(shown) => <ul className={cn("grid items-start gap-1.5 px-2 pt-0.5", wide ? "grid-cols-[repeat(auto-fill,4.4rem)]" : "grid-cols-[repeat(auto-fill,minmax(3.1rem,1fr))]")}>{shown}</ul>}
        rows={items.map((item) => <Tile key={item.key} item={item} titles={titles} fresh={fresh.has(item.key)} />)}
      />
      <Upgrades items={items} titles={titles} />
    </>
  );
}

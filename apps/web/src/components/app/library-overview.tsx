import { useEffect, useRef } from "react";
import { Link } from "react-router-dom";
import { Play, ShieldCheck, ShieldOff, Star } from "lucide-react";

import type { MediaItem } from "../../lib/media-types";
import { cn } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { PosterArtwork } from "./library-grid";
import { TitleMarkChip } from "../ui/title-mark";

/**
 * The third layout: one wide row per title, with room for the synopsis.
 *
 * <p>A poster grid cannot answer "what is this one about", and the compact list
 * has no room to — it is built for file facts. Radarr's third view is exactly
 * this gap, and #310 counted it: 3 views against Deluno's 2.</p>
 *
 * <p>It is not a bigger grid card. The proportions are different on purpose:
 * artwork small enough to be a thumbnail rather than the subject, and a text
 * column wide enough that a paragraph reads as a paragraph. That is the whole
 * reason for a third layout rather than a fourth poster size.</p>
 */
export function LibraryOverview({
  items,
  selectedIds,
  isComplete,
  onSelect,
  onToggle,
  onEndReached
}: {
  items: MediaItem[];
  selectedIds: string[];
  isComplete: boolean;
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
  onEndReached: () => void;
}) {
  const sentinel = useRef<HTMLDivElement | null>(null);

  // The same bargain the grid makes: the shelf is the whole library, so the
  // next slice is fetched when the end of this one comes into view rather than
  // by a page control. `isComplete` is what stops it asking forever.
  useEffect(() => {
    const node = sentinel.current;
    if (!node || isComplete) return;

    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) onEndReached();
    }, { rootMargin: "600px" });

    observer.observe(node);
    return () => observer.disconnect();
  }, [isComplete, onEndReached, items.length]);

  return (
    <div className="space-y-[var(--grid-gap)]">
      {items.map((item) => (
        <OverviewRow
          key={item.id}
          item={item}
          selected={selectedIds.includes(item.id)}
          onSelect={onSelect}
          onToggle={onToggle}
        />
      ))}
      <div ref={sentinel} aria-hidden="true" />
    </div>
  );
}

function OverviewRow({
  item,
  selected,
  onSelect,
  onToggle
}: {
  item: MediaItem;
  selected: boolean;
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
}) {
  const href = item.type === "movie" ? `/movies/${item.id}` : `/tv/${item.id}`;

  return (
    <div
      className={cn(
        "group relative flex gap-[var(--grid-gap)] overflow-hidden rounded-xl border bg-card p-[var(--tile-pad)] transition",
        "hover:border-primary/35 hover:bg-surface-2",
        selected ? "border-primary/70 ring-1 ring-inset ring-primary/40" : "border-hairline"
      )}
    >
      {/*
        The backdrop as ambience down the right edge, not behind the text.

        Stretched across the whole row it was worse than nothing: a 16:9 image
        in a 2224x208 box is cropped to a thin horizontal slice and scaled up to
        fill it, so every row showed one enormous magnified eye or mouth. Three
        rows of that reads as a rendering fault, not as atmosphere.

        Confined to the right third and faded out to the left, it is colour and
        movement behind the badges and nothing behind the synopsis — which is
        the only place on this row where a picture competes with words.
      */}
      {item.backdrop ? (
        <div
          aria-hidden="true"
          className="pointer-events-none absolute inset-y-0 right-0 w-1/3 overflow-hidden"
          style={{ maskImage: "linear-gradient(to right, transparent, black)", WebkitMaskImage: "linear-gradient(to right, transparent, black)" }}
        >
          <img src={item.backdrop} alt="" className="h-full w-full object-cover opacity-[0.14]" />
        </div>
      ) : null}

      <button
        type="button"
        onClick={(event) => (event.metaKey || event.ctrlKey ? onToggle(item.id) : onSelect(item))}
        className="relative w-[5.5rem] shrink-0 overflow-hidden rounded-lg sm:w-[7rem]"
        aria-label={`Open ${item.title}`}
      >
        <PosterArtwork src={item.poster} title={item.title} className="aspect-[2/3] h-full w-full" />
      </button>

      <div className="relative min-w-0 flex-1">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="min-w-0">
            <button
              type="button"
              onClick={(event) => (event.metaKey || event.ctrlKey ? onToggle(item.id) : onSelect(item))}
              className="text-left"
            >
              <p className="line-clamp-1 font-display text-[length:var(--type-title-sm)] font-semibold text-foreground">
                {item.title}
              </p>
            </button>
            <div className="mt-0.5 flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[length:var(--type-body-sm)] text-muted-foreground">
              <span className="tabular">{item.year}</span>
              <span className="text-foreground/20">·</span>
              <span className="inline-flex items-center gap-1">
                {item.monitored ? <ShieldCheck className="h-3 w-3" /> : <ShieldOff className="h-3 w-3" />}
                {item.monitored ? "Monitored" : "Not monitored"}
              </span>
              {item.runtimeMinutes ? (
                <>
                  <span className="text-foreground/20">·</span>
                  <span>{runtimeLabel(item.runtimeMinutes)}</span>
                </>
              ) : null}
              {item.rating !== null && item.rating !== undefined ? (
                <>
                  <span className="text-foreground/20">·</span>
                  <span className="tabular inline-flex items-center gap-0.5 font-semibold text-foreground">
                    <Star className="h-2.5 w-2.5 fill-warning text-warning" />
                    {item.rating.toFixed(1)}
                  </span>
                </>
              ) : null}
            </div>
          </div>

          <div className="flex shrink-0 items-center gap-1.5">
            {item.quality ? (
              <Badge className="whitespace-nowrap bg-surface-2 px-1.5 py-0 text-[length:var(--type-caption)] font-bold text-foreground">
                {item.quality}
              </Badge>
            ) : null}
            <TitleMarkChip item={item} />
          </div>
        </div>

        {/*
          The reason this layout exists. Three lines, because one is a tease
          and the whole thing is a wall — and a title with no synopsis says so
          rather than leaving a gap the eye reads as a loading state.
        */}
        <p className="mt-2 line-clamp-3 text-[length:var(--type-body-sm)] leading-relaxed text-muted-foreground">
          {item.overview?.trim()
            || "No synopsis has been stored yet. Refresh metadata when you want Deluno to enrich this title."}
        </p>

        {item.genres.length > 0 ? (
          <div className="mt-2 flex flex-wrap gap-1">
            {item.genres.slice(0, 4).map((genre) => (
              <span
                key={genre}
                className="rounded-full border border-hairline px-2 py-0.5 text-[length:var(--type-caption)] text-muted-foreground"
              >
                {genre}
              </span>
            ))}
          </div>
        ) : null}
      </div>

      <Link
        to={href}
        onClick={(event) => event.stopPropagation()}
        className="relative hidden shrink-0 items-center gap-1 self-center rounded-lg bg-primary px-3 py-1.5 text-[length:var(--type-caption)] font-bold text-primary-foreground opacity-0 transition group-hover:opacity-100 sm:inline-flex"
      >
        <Play className="h-3 w-3" fill="currentColor" />
        Open
      </Link>
    </div>
  );
}

/** Hours and minutes, the way a person says them. */
function runtimeLabel(minutes: number) {
  const hours = Math.floor(minutes / 60);
  return hours > 0 ? `${hours}h ${minutes % 60}m` : `${minutes}m`;
}

import { Play, ShieldCheck, Star } from "lucide-react";
import { useEffect, useLayoutEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { MediaItem } from "../../lib/media-types";
import type { Density } from "../../lib/use-density";
import { cn } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { TitleMarkBar, TitleMarkChip, TitleMarkDot } from "../ui/title-mark";

export type CardSize = "sm" | "md" | "lg";
export interface DisplayOptions {
  showTitle: boolean;
  showMeta: boolean;
  showStatusPill: boolean;
  showQualityBadge: boolean;
  showRating: boolean;
}

const GRID_MIN_BY_DENSITY: Record<Density, Record<CardSize, string>> = {
  compact: { sm: "var(--library-card-sm)", md: "var(--library-card-md)", lg: "var(--library-card-lg)" },
  comfortable: { sm: "var(--library-card-sm)", md: "var(--library-card-md)", lg: "var(--library-card-lg)" },
  spacious: { sm: "var(--library-card-sm)", md: "var(--library-card-md)", lg: "var(--library-card-lg)" },
  expanded: { sm: "var(--library-card-sm)", md: "var(--library-card-md)", lg: "var(--library-card-lg)" }
};

const TITLE_CLASS_BY_DENSITY: Record<Density, Record<CardSize, string>> = {
  compact: { sm: "text-[length:var(--library-title-sm)]", md: "text-[length:var(--library-title-md)]", lg: "text-[length:var(--library-title-lg)]" },
  comfortable: { sm: "text-[length:var(--library-title-sm)]", md: "text-[length:var(--library-title-md)]", lg: "text-[length:var(--library-title-lg)]" },
  spacious: { sm: "text-[length:var(--library-title-sm)]", md: "text-[length:var(--library-title-md)]", lg: "text-[length:var(--library-title-lg)]" },
  expanded: { sm: "text-[length:var(--library-title-sm)]", md: "text-[length:var(--library-title-md)]", lg: "text-[length:var(--library-title-lg)]" }
};

const SHOW_META: Record<CardSize, boolean> = { sm: false, md: true, lg: true };

/* ═══════════════ PRIMITIVES ═══════════════ */

/**
 * Poster grid with progressive hydration. Renders an initial batch of
 * cards synchronously and then reveals subsequent batches as an
 * intersection sentinel scrolls into view. Keeps first paint cheap
 * when a library has 10k+ titles while still feeling instantaneous.
 */
export function ProgressiveGrid({
  items,
  cardSize,
  density,
  displayOptions,
  selectedIds,
  keyBust,
  onSelect,
  onToggle,
  onEndReached
}: {
  items: MediaItem[];
  cardSize: CardSize;
  density: Density;
  displayOptions: DisplayOptions;
  selectedIds: string[];
  keyBust: string;
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
  onEndReached: () => void;
}) {
  // The scroll container is held in state rather than a ref, because `keyBust`
  // remounts it: a ref would still point at the old node, so the measurement
  // below would keep observing a detached element while the live one grew
  // unmeasured. A state-backed ref callback re-runs the effect on the node that
  // is actually on screen.
  const [container, setContainer] = useState<HTMLDivElement | null>(null);
  const [columns, setColumns] = useState(4);
  const gridMin = GRID_MIN_BY_DENSITY[density][cardSize];

  useLayoutEffect(() => {
    if (!container) return;

    const updateColumns = () => {
      // A detached or not-yet-laid-out container measures zero, and zero divided
      // through says "one column" — which is how clicking a quick filter used to
      // blow the grid up to a single poster the width of the page. It is not a
      // measurement, so it must not become one: keep the last real answer and
      // wait for the next observation.
      const width = container.clientWidth;
      if (width <= 0) return;

      // Resolve the density-aware CSS values in the same element that owns the
      // grid. `getComputedStyle` exposes custom properties as expressions, so a
      // hidden probe is the reliable way to obtain their computed pixel values.
      const probe = document.createElement("div");
      probe.style.cssText = `position:absolute;visibility:hidden;pointer-events:none;min-width:${gridMin};margin-left:var(--library-grid-gap);`;
      container.appendChild(probe);
      const minimumCardWidth = probe.getBoundingClientRect().width;
      const gap = Number.parseFloat(getComputedStyle(probe).marginLeft) || 0;
      probe.remove();

      // A NaN or Infinite column count reached `useVirtualizer` as its row
      // count, which builds an array of that length and threw "Invalid array
      // length", taking the whole Movies route down behind the error boundary
      // (#270). One column is the honest fallback for a track we cannot measure.
      const track = minimumCardWidth + gap;
      const measured = track > 0 ? Math.floor((width + gap) / track) : 1;
      const nextColumns = Number.isFinite(measured) ? Math.max(1, measured) : 1;
      setColumns((current) => current === nextColumns ? current : nextColumns);
    };

    updateColumns();
    const observer = new ResizeObserver(updateColumns);
    observer.observe(container);
    return () => observer.disconnect();
  }, [container, gridMin]);
  // Belt and braces: the virtualiser allocates an array of this length, so it
  // must be a non-negative integer no matter what the measurement produced.
  const safeColumns = Number.isFinite(columns) && columns >= 1 ? Math.floor(columns) : 1;
  const rowCount = Math.max(0, Math.ceil(items.length / safeColumns));
  const virtualizer = useVirtualizer({ count: rowCount, getScrollElement: () => container, estimateSize: () => cardSize === "lg" ? 440 : cardSize === "sm" ? 245 : 340, overscan: 3 });
  const virtualRows = virtualizer.getVirtualItems();

  useEffect(() => {
    const lastRow = virtualRows.at(-1);
    if (lastRow && lastRow.index >= rowCount - 2) onEndReached();
  }, [onEndReached, rowCount, virtualRows]);

  return (
    <>
      <div ref={setContainer} className="max-h-[calc(100dvh-260px)] overflow-auto" key={keyBust}>
        <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
          {virtualRows.map((row) => (
            <div key={row.key} ref={virtualizer.measureElement} data-index={row.index} className="absolute left-0 top-0 w-full" style={{ transform: `translateY(${row.start}px)` }}>
              <div className="stagger grid gap-[var(--library-grid-gap)] pb-[var(--library-grid-gap)]" style={{ gridTemplateColumns: `repeat(${safeColumns}, minmax(${gridMin}, 1fr))` }}>
                {items.slice(row.index * safeColumns, (row.index + 1) * safeColumns).map((item) => (
                  <PosterCard key={item.id} item={item} size={cardSize} density={density} displayOptions={displayOptions} selected={selectedIds.includes(item.id)} onSelect={() => onSelect(item)} onToggle={() => onToggle(item.id)} />
                ))}
              </div>
            </div>
          ))}
        </div>
      </div>
    </>
  );
}

function PosterCard({
  item,
  size = "md",
  density,
  displayOptions,
  selected,
  onSelect,
  onToggle
}: {
  item: MediaItem;
  size?: CardSize;
  density: Density;
  displayOptions: DisplayOptions;
  selected: boolean;
  onSelect: () => void;
  onToggle: () => void;
}) {
  const workspaceHref = item.type === "movie" ? `/movies/${item.id}` : `/tv/${item.id}`;
  const showMeta = SHOW_META[size] && displayOptions.showMeta;
  const titleCls = TITLE_CLASS_BY_DENSITY[density][size];

  return (
    <div className="group relative">
      {/* Premium circular selection toggle */}
      <button
        type="button"
        onClick={(e) => { e.stopPropagation(); onToggle(); }}
        aria-label={selected ? "Deselect" : "Select"}
        className={cn(
          "absolute left-2 top-2 z-10 flex shrink-0 items-center justify-center rounded-full transition-all duration-200",
          size === "sm" ? "h-5 w-5" : "h-6 w-6",
          selected
            ? [
                "bg-gradient-to-br from-primary to-[hsl(var(--primary-2))] text-primary-foreground",
                "opacity-100 scale-100",
                "shadow-[0_0_0_2px_hsl(0_0%_0%/0.4),0_0_12px_hsl(var(--primary)/0.6),inset_0_1px_0_hsl(0_0%_100%/0.25)]"
              ].join(" ")
            : [
                "border border-white/25 bg-black/50 text-white/0 backdrop-blur-md",
                "opacity-0 scale-90 group-hover:opacity-100 group-hover:scale-100"
              ].join(" ")
        )}
      >
        {selected ? (
          /* Custom clean checkmark */
          <svg width="10" height="8" viewBox="0 0 10 8" fill="none" className="shrink-0">
            <path d="M1.5 4L4 6.5L8.5 1.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        ) : (
          <svg width="10" height="8" viewBox="0 0 10 8" fill="none" className="shrink-0 opacity-60">
            <path d="M1.5 4L4 6.5L8.5 1.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
          </svg>
        )}
      </button>

      <button
        type="button"
        onClick={onSelect}
        className="block w-full text-left"
      >
        <div
          className={cn(
            "relative aspect-[2/3] overflow-hidden rounded-xl bg-muted transition-all duration-300",
            "shadow-card group-hover:-translate-y-1 group-hover:shadow-lg",
            selected
              ? "ring-2 ring-primary/80 shadow-[0_0_0_3px_hsl(var(--primary)/0.15),0_0_28px_hsl(var(--primary)/0.35)]"
              : "ring-0"
          )}
        >
          {/* Selected scrim overlay */}
          {selected && (
            <div className="pointer-events-none absolute inset-0 z-[5] bg-gradient-to-b from-primary/15 to-transparent" />
          )}
          <PosterArtwork
            src={item.poster}
            title={item.title}
            className="h-full w-full transition-transform duration-500 group-hover:scale-[1.04]"
          />

          {/*
            One dot and one bar, and nothing else — see DESIGN-001.

            The dot was a lifecycle *chip* derived from `status`, which only ever
            said whether a file existed: a movie below its target quality looked
            identical to a finished one, and monitoring had to be repeated as
            supporting text underneath because a chip could not carry it. The dot
            says which of the four rungs the title is on, and a half says you are
            not monitoring it.
          */}
          {displayOptions.showStatusPill ? (
            <div className="absolute right-1.5 top-1.5 z-10">
              {size === "sm" ? (
                <TitleMarkDot item={item} size={13} />
              ) : (
                <TitleMarkChip item={item} />
              )}
            </div>
          ) : null}

          {/* What you asked for beyond the title. A movie has no bar. */}
          <TitleMarkBar item={item} />

          {/* Gradient overlay — condenses on small */}
          <div className={cn(
            "absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/95 via-black/55 to-transparent",
            size === "sm" ? "px-2 pb-2 pt-8" : "px-2.5 pb-2.5 pt-14"
          )}>
            {displayOptions.showTitle ? (
              <p className={cn("line-clamp-2 font-semibold leading-tight text-[hsl(var(--media-foreground))] drop-shadow", titleCls)}>
                {item.title}
              </p>
            ) : null}
            {showMeta ? (
              <div className="mt-0.5 flex items-center gap-1.5 text-[length:var(--library-meta-size)] text-[hsl(var(--media-muted-foreground))]">
                <span className="tabular">{item.year}</span>
                <span className="text-[hsl(var(--media-muted-foreground)/0.45)]">·</span>
                <span className="inline-flex items-center gap-1" title={item.monitored ? "Deluno will keep looking for this title." : "Deluno will not search for this title automatically."}>
                  <ShieldCheck className="h-3 w-3 text-[hsl(var(--media-muted-foreground))]" />
                  {item.monitored ? "Monitored" : "Not monitored"}
                </span>
              </div>
            ) : null}
            {showMeta && (displayOptions.showRating || displayOptions.showQualityBadge) ? (
              <div className="mt-1">
                <div className="flex items-center justify-between gap-2 text-[length:var(--library-meta-size)]">
                  {displayOptions.showRating && item.rating !== null ? (
                    <span className="tabular inline-flex items-center gap-0.5 font-bold text-[hsl(var(--media-foreground))]">
                      <Star className="h-2.5 w-2.5 fill-warning text-warning" />
                      {item.rating.toFixed(1)}
                    </span>
                  ) : <span />}
                  {displayOptions.showQualityBadge && item.quality ? (
                    <Badge className="bg-white/15 px-1.5 py-0 text-[length:var(--library-badge-size)] font-bold text-[hsl(var(--media-foreground))] backdrop-blur-sm">
                      {shortQuality(item.quality)}
                    </Badge>
                  ) : null}
                </div>
              </div>
            ) : null}
          </div>

          {/* Hover-reveal action row */}
          <div className="absolute inset-x-0 bottom-0 flex items-center gap-1 bg-gradient-to-t from-black to-transparent px-2 pb-2 pt-6 opacity-0 transition-opacity duration-300 group-hover:opacity-100">
            <Link
              to={workspaceHref}
              onClick={(e) => e.stopPropagation()}
              className="flex flex-1 items-center justify-center gap-1 rounded-lg bg-primary px-2 py-1.5 text-[length:var(--library-badge-size)] font-bold text-primary-foreground shadow-md transition hover:brightness-110"
            >
              <Play className="h-2.5 w-2.5" fill="currentColor" />
              Open
            </Link>
          </div>
        </div>
      </button>

      {/* Below-poster metadata — adapts per size */}
      <div className="hidden">
        {displayOptions.showTitle ? (
          <p className={cn("line-clamp-1 font-semibold text-foreground", titleCls)}>
            {item.title}
          </p>
        ) : null}
        {showMeta ? (
          <div className="flex items-center gap-1.5 text-[length:var(--library-meta-size)] text-muted-foreground">
            <span className="tabular">{item.year}</span>
            <span className="text-foreground/20">·</span>
            <span className="inline-flex items-center gap-1" title={item.monitored ? "Deluno will keep looking for this title." : "Deluno will not search for this title automatically."}>
              <ShieldCheck className="h-3 w-3" />
              {item.monitored ? "Monitored" : "Not monitored"}
            </span>
          </div>
        ) : null}
      </div>
    </div>
  );
}

export function PosterArtwork({
  src,
  title,
  className,
  compact = false
}: {
  src: string | null;
  title: string;
  className?: string;
  compact?: boolean;
}) {
  if (src) {
    return <img src={src} alt={title} className={cn("object-cover", className)} loading="lazy" />;
  }

  return (
    <div
      className={cn(
        "flex items-center justify-center bg-gradient-to-br from-surface-2 to-surface-3 text-center text-muted-foreground",
        className
      )}
      aria-label={`${title} artwork unavailable`}
    >
      <span className={cn("px-2 font-display font-semibold tracking-tight", compact ? "text-[length:var(--type-micro)]" : "text-sm")}>
        {title.slice(0, 2).toUpperCase()}
      </span>
    </div>
  );
}

export function shortQuality(value: string) {
  if (value.includes("2160")) return "4K";
  if (value.includes("1080")) return "1080p";
  if (value.includes("720")) return "720p";
  return value;
}


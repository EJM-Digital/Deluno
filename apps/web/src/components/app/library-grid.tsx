import {
  CalendarDays, Clapperboard, Disc, HardDrive, ListVideo, MonitorPlay, Play,
  ShieldCheck, ShieldOff, Star, Tag, Timer, Tv, Users, RefreshCw, Search} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useEffect, useLayoutEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { MediaItem } from "../../lib/media-types";
import { buildJumpBuckets } from "../../lib/library-buckets";
import type { SortField } from "../../lib/library-filters";
import { JumpRail, useJumpRail } from "./library-jump-rail";
import { heldQualityLabel } from "../../lib/quality-label";
import type { Density } from "../../lib/use-density";
import { authedFetch } from "../../lib/use-auth";
import { cn } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { TitleMarkBar, TitleMarkTopBar } from "../ui/title-mark";

export type CardSize = "sm" | "md" | "lg";

/**
 * Re-exported, not redeclared. This was its own copy of the same five fields
 * while `lib/library-filters.ts` declared another — the defect that file's own
 * header describes, one import below it.
 */
import type { DisplayOptions } from "../../lib/library-filters";
import { TITLE_MARK_PRESENTATION, titleMark } from "../../lib/status-tones";
export type { DisplayOptions } from "../../lib/library-filters";

/**
 * The rating sources a poster can carry, matching the server's
 * `RatingSources.All` — the ids come from the served poster options, so a source
 * added on the server appears here as a toggle whose number this can draw.
 */
const RELEASE_DATES = [
  { option: "showInCinemas", field: "inCinemas", label: "Cinemas", icon: Clapperboard },
  { option: "showDigitalRelease", field: "digitalRelease", label: "Digital", icon: MonitorPlay },
  { option: "showPhysicalRelease", field: "physicalRelease", label: "Disc", icon: Disc }
] as const;

const RATING_SOURCES = [
  { option: "showRatingtmdb", field: "tmdbRating", label: "TMDb", outOf: 10 },
  { option: "showRatingimdb", field: "imdbRating", label: "IMDb", outOf: 10 },
  { option: "showRatingrottentomatoes", field: "tomatoRating", label: "RT", outOf: 100 },
  { option: "showRatingmetacritic", field: "metacriticRating", label: "Metacritic", outOf: 100 }
] as const;

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
 * One continuous shelf, virtualised.
 *
 * This used to be one hundred titles behind `Previous 100` / `Next 100`, and a
 * line of copy calling that a feature. Reaching title 3,000 of 6,000 was thirty
 * round trips and Ctrl+F found one page of them. Radarr renders its whole
 * 5,279-title library in one page behind a three-to-five second witty message
 * and is better for it — so the shelf is now the whole library too, fed in the
 * background by the same keyset query and drawn a screen at a time, which is the
 * part Radarr pays five seconds for and this does not.
 */
export function ProgressiveGrid({
  items,
  cardSize,
  density,
  displayOptions,
  selectedIds,
  keyBust,
  sortField,
  sortDirection,
  isComplete,
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
  sortField: SortField;
  sortDirection: "asc" | "desc";
  isComplete: boolean;
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
      // The probe goes in the container's *parent*, not the container.
      //
      // The custom properties cascade, so it measures the same thing either
      // way — but the container is the element this ResizeObserver watches, and
      // adding a child to it inside its own callback is a write to the thing
      // being observed. It is the second half of the loop James saw shaking.
      const host = container.parentElement ?? container;
      const probe = document.createElement("div");
      probe.style.cssText = `position:absolute;visibility:hidden;pointer-events:none;min-width:${gridMin};margin-left:var(--library-grid-gap);`;
      host.appendChild(probe);
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

  const buckets = useMemo(
    () => buildJumpBuckets(items, sortField, sortDirection),
    [items, sortDirection, sortField]
  );
  const { slotWidth, activeIndex, jumpTo } = useJumpRail(virtualizer, safeColumns, virtualRows, buckets);

  return (
    <div className="flex items-stretch gap-1">
      {/*
        Room at the top for the hover lift. `-translate-y-1` moves a card 4px up,
        and without this the top row lifts out of the scroll box and is cut.
      */}
      <div ref={setContainer} className="max-h-[calc(100dvh-260px)] min-w-0 flex-1 overflow-auto pt-1.5" key={keyBust}>
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
      {/*
        Always here, and always this wide. Letting either the rail or its slot
        come and going moved the shelf under the reader — see `useJumpRail`.
      */}
      <div className="hidden shrink-0 pl-1 sm:block" style={{ width: slotWidth }}>
        <JumpRail buckets={buckets} activeIndex={activeIndex} isComplete={isComplete} onJump={jumpTo} />
      </div>
    </div>
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
  const apiSegment = item.type === "movie" ? "movies" : "series";

  // Which action is in flight, and what the last one said.
  //
  // Held on the card rather than lifted to the shelf: a search on one poster
  // says nothing about the others, and threading a callback per action through
  // the grid would make every card re-render when any one of them was clicked.
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [actionResult, setActionResult] = useState<string | null>(null);

  async function runAction(name: string, url: string) {
    if (busyAction) return;

    setBusyAction(name);
    setActionResult(null);

    try {
      const response = await authedFetch(url, { method: "POST" });
      if (!response.ok) throw new Error(name);

      // What it did, not what it is doing. "Searching" was a lie by the time
      // it was drawn - the request had already come back.
      setActionResult(name === "search" ? await searchOutcome(response) : "Refreshed");
    } catch {
      setActionResult("Failed");
    } finally {
      setBusyAction(null);
      // Long enough to read, short enough that it is gone before you look
      // back. Nothing else on the card moves while it is there.
      window.setTimeout(() => setActionResult(null), 3200);
    }
  }
  // Whether this card size draws anything under the artwork at all. Small is
  // deliberately title-only. It is not a switch — the switches decide *what*
  // is drawn, and this decides whether there is room to draw it.
  const carriesMeta = SHOW_META[size];
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
            // `ring-inset`, so selection paints *inside* the poster.
            //
            // It was an outward `ring-2` plus a 3px shadow spread, and the grid
            // scrolls inside an `overflow-auto` container — so anything drawn
            // outside a card's box is clipped by it. Hovering made it worse:
            // `-translate-y-1` lifts the card 4px, which on the top row moved it
            // straight out of the scroll box, and the border vanished while the
            // artwork looked cropped. Measured on the rig: container top 289px,
            // selected card top 285px.
            //
            // Drawn inward it cannot be clipped, at any scroll position, in any
            // row. The glow stays soft and outward — a blurred edge fading out
            // is not something you can see being cut.
            selected
              ? "ring-2 ring-inset ring-primary/90 shadow-[0_0_24px_hsl(var(--primary)/0.35)]"
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
            The state, as a bar across the top of the artwork.

            It was a dot in the corner — nine pixels carrying the most important
            fact on the card — with the tier as a separate pill at the foot, so
            there were three things to learn and two of them were tiny. James,
            after looking at four rendered treatments: "I like a combination of
            1 and 2... if quality is not ticked it goes to the small bar, if its
            ticked it goes to the big bar with the quality or status".

            So the mark is a bar, and the Quality switch decides its size:

              Quality off  →  a thin strip, colour only
              Quality on   →  a full bar carrying the tier you hold, or the
                              state's own word when there is no file to name

            The colour is the state's either way — gold for Quality met, green
            for Upgradable — read through `titleMark`, the same source the
            legend and the detail page use. Half-width for a title Deluno is not
            watching, exactly as the dot went half.

            With the subtitle bar on the bottom edge the two book-end the
            artwork, which is what the renders were for.
          */}
          {displayOptions.showStatusPill || displayOptions.showQualityBadge ? (
            <TitleMarkTopBar
              item={item}
              // Only the full bar carries words, and only when there is a tier
              // to name or a state worth naming.
              label={displayOptions.showQualityBadge
                ? heldQualityLabel(item) ?? qualityTone(item).label
                : null}
            />
          ) : null}

          {/* What you asked for beyond the title. A movie has no bar. */}
          <TitleMarkBar item={item} />

          {/*
            The quality stays on the artwork, and nothing else does.

            James: "I think putting it ALL on the poster is going to be a huge
            mess but I could be wrong." He was right, and Radarr is the evidence
            — it prints its metadata under the artwork and keeps the poster for
            the artwork and one status bar. Everything that used to live in a
            gradient over the bottom third of the image now sits below it.

            The three that stayed are the three you read *while scanning*: the
            mark, the subtitle bar and the tier you hold. They are states, they
            are short, and they are what the eye is hunting for. Title and year
            are not — you read those once you have stopped, which is what the
            block under the poster is.
          */}
          {/*
            The tier you hold, across the foot of the artwork.

            It was a small grey pill in the bottom-left corner, and grey is the
            one thing it should not be — James: "if its quality met its gold, if
            its upgradeable its green". Those colours already exist and already
            mean that; the badge was the one place on the card ignoring them.

            So it takes its colour from the title's own mark, which is the same
            source the dot and the chip read. Gold for Quality met, green for
            Upgradable, red for Missing — and it can never disagree with the mark
            above it, because there is one answer and both draw from it.

            Full width and centred rather than tucked in a corner: it is the
            second thing you look for after the artwork, and Radarr puts its
            equivalent exactly here for the same reason.
          */}
          {/*
            The actions, as a small cluster in the middle of the artwork.

            It was one full-width "Open" bar across the foot of the poster,
            which is a lot of paint for one verb and sat on top of the two bars
            that carry state and subtitles. James: "I think the open button
            should be a smaller button in the center of the poster too - look at
            what radarr does we should do the same".

            Radarr puts three or four round icon buttons in the middle on hover
            and nothing at the edges, which leaves the top and bottom to the
            things that are always drawn. These are the three Deluno already has
            a per-title endpoint for.

            The scrim is the whole poster rather than a foot gradient: at the
            centre there is artwork behind the buttons, not a dark edge, and
            without it a white poster swallows them.
          */}
          <div className="pointer-events-none absolute inset-0 z-20 flex items-center justify-center gap-1.5 bg-black/45 opacity-0 backdrop-blur-[1px] transition-opacity duration-200 group-hover:opacity-100 focus-within:opacity-100">
            <PosterAction
              label="Open"
              icon={Play}
              href={workspaceHref}
            />
            <PosterAction
              label={`Search for ${item.title} now`}
              icon={Search}
              busy={busyAction === "search"}
              // The automatic search, deliberately - no `?mode=preview`.
              // James: "the search should be an automatic search and not an
              // interactive one fyi". Interactive returns candidates for a
              // person to choose between, which needs a screen; this button
              // means "go and get it".
              onClick={() => void runAction("search", `/api/${apiSegment}/${item.id}/search`)}
            />
            <PosterAction
              label={`Refresh metadata for ${item.title}`}
              icon={RefreshCw}
              busy={busyAction === "refresh"}
              onClick={() => void runAction("refresh", `/api/${apiSegment}/${item.id}/metadata/refresh`)}
            />

            {actionResult ? (
              <span
                role="status"
                className="pointer-events-none absolute inset-x-2 bottom-3 truncate text-center text-[length:var(--library-badge-size)] font-bold uppercase tracking-wider text-white"
              >
                {actionResult}
              </span>
            ) : null}
          </div>
        </div>
      </button>

      {/*
        Below-poster metadata — where everything you read once you have stopped
        lives now.

        This block existed as `<div className="hidden">` for months: the markup
        for it was written and then never switched on, while the same fields
        were painted into a gradient over the artwork instead. #310 is what
        turned it on, and it is also what makes fourteen poster switches
        survivable — the extras were crushed into one truncated sentence
        precisely because there was no room over the image, and here there is.
      */}
      {/*
        Tight, the way Radarr's is. Every row sits directly under the last with
        no gap between them: with a dozen switches available, four pixels of
        breathing room per row turns a compact block into a column of holes,
        and an empty reserved row stops reading as spacing and starts reading
        as a fault. Compared side by side with Radarr this was the whole
        difference.
      */}
      <div className="mt-2 min-w-0 text-center">
        {displayOptions.showTitle ? (
          // One line, truncated — the same rule as every row beneath it.
          //
          // This wrapped to two lines and reserved the space on every card so
          // the rows below stayed level. That worked and it cost a blank line
          // under every short title, which is the gap James circled: "truncate
          // long names so there is no gap between the details and as I said, 1
          // per line". One line for the title means no reservation to make, and
          // the metadata starts in the same place on every card because there
          // is only ever one line above it.
          //
          // The full name is in the tooltip, and the title is also the card's
          // accessible label, so nothing is lost to the ellipsis.
          <p
            className={cn("h-[1lh] truncate font-semibold leading-tight text-foreground", titleCls)}
            title={item.title}
          >
            {item.title}
          </p>
        ) : null}

        {/*
          The year is gone.

          It was drawn by the same switch as the monitored state — "Year and
          monitoring" — so the two were welded together and neither could be
          turned off without the other. James: "year should be removed as a not
          required option and it should not be aligned to monitored or not
          monitored." It is not required, and pairing it with monitoring was the
          part that made it feel mandatory.

          The switch is now Monitoring, and it does one thing.
        */}
        {carriesMeta && displayOptions.showMonitored ? (
          <div
            className="flex h-[1lh] min-w-0 items-center justify-center gap-1 leading-tight text-[length:var(--library-meta-size)] text-muted-foreground"
            title={item.monitored
              ? "Deluno will keep looking for this title."
              : "Deluno will not search for this title automatically."}
          >
            {item.monitored
              ? <ShieldCheck className="h-3 w-3 shrink-0 opacity-70" aria-hidden="true" />
              : <ShieldOff className="h-3 w-3 shrink-0 opacity-70" aria-hidden="true" />}
            <span className="truncate">{item.monitored ? "Monitored" : "Not monitored"}</span>
          </div>
        ) : null}

        {carriesMeta ? <PosterExtras item={item} displayOptions={displayOptions} /> : null}
      </div>
    </div>
  );
}

/**
 * When the next episode is due, in the shortest form that is still unambiguous.
 *
 * Sonarr prints a weekday for anything inside the coming week and a date beyond
 * it, which is the right instinct: "Thursday" is what you actually want to know
 * about a show airing this week, and useless about one returning in October.
 *
 * A date already past is not printed at all. It means the episode aired and
 * nothing has recomputed the show yet — a window of at most a few minutes — and
 * printing "last Tuesday" under a poster would be worse than printing nothing.
 */
function nextAiringLabel(nextAirDateUtc: string): string | null {
  const next = new Date(nextAirDateUtc);
  if (Number.isNaN(next.getTime())) return null;

  const days = (next.getTime() - Date.now()) / 86_400_000;
  if (days < 0) return null;

  return days < 7
    ? next.toLocaleDateString(undefined, { weekday: "long" })
    : next.toLocaleDateString(undefined, { day: "numeric", month: "short" });
}

/**
 * Whatever else this reader asked a poster to carry, in one truncated line.
 *
 * Everything here is a fact about the file or the title that Deluno already
 * holds and had nowhere to show — James: "we also need more options for what
 * the posters can display from the metadata". Nothing is invented and nothing
 * is guessed: a value that is not there is simply absent from the line, rather
 * than printing "Unknown" and claiming it could not be read.
 */
/**
 * One row per switch, and nothing shares a row.
 *
 * <p>James: "all the what posters should show should be 1 under the other and
 * not next to each other, nothing shares a row." These used to be joined into a
 * single truncated sentence, which was the right shape when they were painted
 * over the artwork and there was room for exactly one line. Under the poster
 * there is room, and a sentence made of six unrelated facts is harder to read
 * than six lines of one fact each.</p>
 *
 * <p>Every enabled row is drawn <b>whether or not this title has a value for
 * it</b>. A film with no file has no size and its neighbour does; dropping the
 * empty one would make the two cards different heights, which is the
 * misalignment this was fixed for in the first place.</p>
 */
function PosterExtras({ item, displayOptions }: { item: MediaItem; displayOptions: DisplayOptions }) {
  // Every switch that is on gets a row on every card, whether or not this
  // title has a value for it.
  //
  // I had this reserved, took the reservation out to close the gaps James
  // circled, and broke something worse: with rows dropped, row four is the size
  // on one card and the runtime on the next, so a column means two different
  // things depending on which title you are looking at. James: "the columns
  // should mean the same thing for every card Im kind of shocked why they
  // dont?" — correct, and the gaps were never the real problem.
  //
  // What made the blanks look like a fault was that they said nothing. A row
  // that reads "—" is a stated absence: the switch is on, this is that fact,
  // and this title has none. It is also the only way to tell a switch that does
  // nothing from a switch that is broken — five of them produce no value on a
  // library with no release groups in its filenames and no OMDb key, and
  // silence made all five look like defects.
  const rows = posterRowsFor(item, displayOptions);

  if (rows.length === 0) return null;

  return (
    <>
      {rows.map((row) => {
        const { value } = row;
        const Icon = row.icon;

        return (
          <div
            key={row.option}
            className="flex h-[1lh] min-w-0 items-center justify-center gap-1 leading-tight text-[length:var(--library-meta-size)] text-muted-foreground"
            title={value ? `${row.label}: ${value}` : `${row.label}: nothing known for this title`}
          >
            {/* An icon rather than a word. "Cinemas Nov 10, 2016" spends half a
                narrow card saying which date it is; Radarr uses a glyph and
                fits four dates where this fitted two. The word survives in the
                tooltip, so nothing is lost for anyone who needs it.

                Nothing is drawn at all when there is no value — an icon on its
                own points at a fact that is not there, which reads worse than
                the blank line it was meant to explain. The row keeps its height
                either way, which is what holds the cards level. */}
            <Icon className={cn("h-3 w-3 shrink-0", value ? "opacity-70" : "opacity-30")} aria-hidden="true" />
            <span className={cn("truncate", value ? "" : "opacity-40")}>{value ?? "—"}</span>
          </div>
        );
      })}
    </>
  );
}

/**
 * What a finished search actually did, in one word.
 *
 * <p>On a card two centimetres wide the answer has to be a word, and the useful
 * word is the outcome: whether a release was taken or nothing was found. The
 * detail page is where a search explains itself.</p>
 */
async function searchOutcome(response: Response) {
  try {
    const payload = (await response.json()) as { outcome?: string; releaseName?: string | null };
    // "matched" is what the pipeline writes when a release was taken; every
    // other outcome is effort that came back empty.
    return payload.outcome === "matched" || payload.releaseName ? "Grabbed" : "Nothing found";
  } catch {
    return "Searched";
  }
}

/**
 * One round action on a poster.
 *
 * <p>A link when it navigates and a button when it does something, because
 * those are different things to a keyboard and to a screen reader — and because
 * middle-clicking Open should open a tab, which a button cannot do.</p>
 *
 * <p>The label is only ever a tooltip and an accessible name: at this size a
 * word does not fit, and three glyphs in a row is what Radarr does for the same
 * reason.</p>
 */
function PosterAction({
  label,
  icon: Icon,
  href,
  onClick,
  busy = false
}: {
  label: string;
  icon: LucideIcon;
  href?: string;
  onClick?: () => void;
  busy?: boolean;
}) {
  const className = cn(
    "pointer-events-auto inline-flex h-7 w-7 items-center justify-center rounded-full",
    "bg-black/55 text-white ring-1 ring-inset ring-white/25 backdrop-blur-sm",
    "transition hover:bg-primary hover:text-primary-foreground hover:ring-primary",
    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary",
    busy && "opacity-60"
  );

  const glyph = <Icon className={cn("h-3.5 w-3.5", busy && "animate-spin")} aria-hidden="true" />;

  if (href) {
    return (
      <Link to={href} onClick={(event) => event.stopPropagation()} title={label} aria-label={label} className={className}>
        {glyph}
      </Link>
    );
  }

  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      disabled={busy}
      onClick={(event) => {
        // The card behind this opens the drawer on click.
        event.stopPropagation();
        onClick?.();
      }}
      className={className}
    >
      {glyph}
    </button>
  );
}

/**
 * The title's own mark, for the word the top bar falls back to when there is no
 * file to name a tier from.
 */
function qualityTone(item: MediaItem) {
  return TITLE_MARK_PRESENTATION[titleMark(item)];
}


/**
 * The rows one card draws, in order — one per switch that is on, whether or not
 * this title has a value for it.
 *
 * <p>Exported because it is the rule the cards are judged by and it cannot be
 * checked by looking: a switch that is on and produces nothing is
 * indistinguishable from a broken switch, and a row that is dropped on one card
 * and drawn on the next silently changes what a column means. Both of those got
 * past a browser sweep that measured row counts and alignment.</p>
 */
export function posterRowsFor(item: MediaItem, displayOptions: DisplayOptions) {
  return POSTER_ROWS
    .filter((row) => displayOptions[row.option])
    .map((row) => ({ ...row, value: row.read(item) }));
}

/**
 * Every switch that draws a row beneath the poster, in the order the server
 * declares them.
 *
 * <p>Labelled wherever a bare value would be ambiguous. "2h 44m" and "0.1 GB"
 * speak for themselves; four dates and four scores do not, and an unlabelled
 * column of numbers is worse than no column at all.</p>
 *
 * <p>`RatingPosterOptionsTests` checks this list against the poster options the
 * server declares with `line: true`, so a switch added on one side and not the
 * other fails a test rather than silently drawing nothing.</p>
 */
const POSTER_ROWS: {
  option: string;
  /** Named for the tooltip, because a glyph on its own is a guess. */
  label: string;
  icon: LucideIcon;
  read: (item: MediaItem) => string | null;
}[] = [
  {
    option: "showRating",
    label: "Rating",
    icon: Star,
    read: (item) => item.rating !== null && item.rating !== undefined ? item.rating.toFixed(1) : null
  },
  {
    option: "showSize",
    label: "Size on disk",
    icon: HardDrive,
    // A title with no file has no size, the same rule the compact list follows.
    read: (item) => item.hasFile !== false && typeof item.sizeGb === "number"
      ? `${item.sizeGb.toFixed(1)} GB`
      : null
  },
  {
    option: "showRuntime",
    label: "Runtime",
    icon: Timer,
    read: (item) => item.runtimeMinutes ? runtimeLabel(item.runtimeMinutes) : null
  },
  {
    option: "showGenres",
    label: "Genres",
    icon: Tag,
    // Two, not all of them: a row is one line and a film with six genres would
    // truncate mid-word every time.
    read: (item) => item.genres.length > 0 ? item.genres.slice(0, 2).join(", ") : null
  },
  {
    option: "showReleaseGroup",
    label: "Release group",
    icon: Users,
    read: (item) => item.releaseGroup ?? null
  },
  {
    option: "showCodec",
    label: "Codec",
    icon: Clapperboard,
    // Video and audio together, because they are one question — "what is inside
    // this file" — and the switch is one switch.
    read: (item) => {
      const codecs = [item.codec, item.audioCodec].filter(Boolean);
      return codecs.length > 0 ? codecs.join(" · ") : null;
    }
  },
  {
    option: "showAdded",
    label: "Added to your library",
    icon: CalendarDays,
    // Labelled now that it has a row of its own: on its own line, a bare date
    // could be any of the four this card can show.
    read: (item) => item.added ?? null
  },
  ...RATING_SOURCES.map((source) => ({
    option: source.option,
    label: `${source.label} score`,
    icon: Star,
    read: (item: MediaItem) => {
      const score = item[source.field];
      // The source's own name stays, because four bare numbers in a column
      // are four numbers nobody can attribute.
      return typeof score === "number"
        ? `${source.label} ${source.outOf === 100 ? `${Math.round(score)}%` : score.toFixed(1)}`
        : null;
    }
  })),
  ...RELEASE_DATES.map((release) => ({
    option: release.option,
    label: release.label,
    icon: release.icon,
    read: (item: MediaItem) => {
      const value = item[release.field];
      return value ? releaseDateLabel(value) : null;
    }
  })),
  {
    // How far through a show you are, over what has aired rather than over what
    // will eventually exist: an ongoing series measured against its final
    // episode count reads permanently unfinished, which is true of every
    // ongoing series and therefore says nothing.
    option: "showEpisodeProgress",
    label: "Episodes held",
    icon: ListVideo,
    read: (item) => typeof item.airedEpisodeCount === "number" && item.airedEpisodeCount > 0
      ? `${item.airedWithFileCount ?? 0}/${item.airedEpisodeCount} episodes`
      : null
  },
  {
    option: "showNextAiring",
    label: "Next airing",
    icon: Tv,
    read: (item) => item.nextAirDateUtc ? nextAiringLabel(item.nextAirDateUtc) : null
  }
];

/** Hours and minutes, the way a person says them. */
function runtimeLabel(minutes: number) {
  const hours = Math.floor(minutes / 60);
  return hours > 0 ? `${hours}h ${minutes % 60}m` : `${minutes}m`;
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

/**
 * A release date in the shortest form that is still unambiguous.
 *
 * The year is dropped for dates in the current year and kept otherwise, which
 * is how a person writes them: "12 Mar" for something this year, "12 Mar 2019"
 * for something that is not. An unparseable value is printed as it arrived
 * rather than as "Invalid Date".
 */
function releaseDateLabel(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;

  const sameYear = date.getFullYear() === new Date().getFullYear();
  return date.toLocaleDateString(undefined, {
    day: "numeric",
    month: "short",
    ...(sameYear ? {} : { year: "numeric" })
  });
}

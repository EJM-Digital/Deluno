import { useCallback, useEffect, useRef, useState } from "react";
import type { VirtualItem, Virtualizer } from "@tanstack/react-virtual";
import type { JumpBucket } from "../../lib/library-buckets";
import { cn } from "../../lib/utils";

/**
 * The rail beside the shelf.
 *
 * Radarr renders 5,279 posters in one page with an A–Z rail down the right
 * edge, and that rail is the reason its "no paging" choice works: without it,
 * one continuous list is just a very long scroll. Deluno's shelf is virtualised
 * rather than fully rendered, so the rail is doing the same job over a list the
 * browser never has to draw — and it is not limited to letters. Under any other
 * order the stops are that field's own grain: decades under Year, size bands
 * under Size, ladder rungs under Quality. Radarr has no equivalent, because
 * Radarr's rail only knows the alphabet.
 */
export function JumpRail({
  buckets,
  activeIndex,
  isComplete,
  onJump
}: {
  buckets: JumpBucket[];
  /** Index of the first title currently on screen, so the rail says where you are. */
  activeIndex: number;
  /** Whether the whole library has arrived. Until it has, an empty stop is unknown, not empty. */
  isComplete: boolean;
  onJump: (index: number) => void;
}) {
  // A stop clicked before its titles have loaded is remembered rather than
  // ignored. The shelf fills in order, so the wait is bounded and the click
  // resolves itself — which is the whole reason the rail can be derived from
  // the loaded rows without ever telling a reader "not yet".
  const [pending, setPending] = useState<string | null>(null);
  const onJumpRef = useRef(onJump);
  onJumpRef.current = onJump;

  useEffect(() => {
    if (pending === null) return;
    const arrived = buckets.find((bucket) => bucket.label === pending);
    if (arrived && arrived.index !== null) {
      onJumpRef.current(arrived.index);
      setPending(null);
      return;
    }
    // Nothing behind it after a complete load means nothing behind it.
    if (isComplete) setPending(null);
  }, [buckets, isComplete, pending]);

  if (buckets.length < 2) return null;

  // The last run that has begun by the end of the top row, not the first.
  //
  // A grid row is atomic, so jumping to S lands on a row whose first few cards
  // are still the tail of R. Marking the run the top row *starts in* would then
  // light R immediately after a reader clicked S, which reads as the rail
  // ignoring them. The question a rail answers is "which run have I arrived
  // at", and that is the later one.
  let activeLabel: string | null = null;
  for (const bucket of buckets) {
    if (bucket.index !== null && bucket.index <= activeIndex) activeLabel = bucket.label;
  }

  return (
    <nav
      aria-label="Jump to"
      // Spread down the full height of the shelf rather than clustered in the
      // middle of it. A rail you aim at is a rail: 27 letters over 600px gives
      // every stop a target you can hit without looking, and the same spacing
      // makes six size bands read as a scale rather than a stray menu.
      className="hidden shrink-0 flex-col items-stretch justify-evenly gap-px overflow-y-auto py-1 pl-1 sm:flex"
    >
      {buckets.map((bucket) => {
        const loaded = bucket.index !== null;
        // Inert only once we know: greying "W" out while the shelf is still
        // filling would be the rail lying for the two seconds that matter most.
        const inert = !loaded && isComplete;
        const waiting = pending === bucket.label;

        return (
          <button
            key={bucket.label}
            type="button"
            disabled={inert}
            aria-current={activeLabel === bucket.label ? "true" : undefined}
            title={
              inert
                ? `No titles under ${bucket.label}`
                : loaded
                  ? `${bucket.label} — ${bucket.count.toLocaleString()} ${bucket.count === 1 ? "title" : "titles"}`
                  : `${bucket.label} — still loading`
            }
            onClick={() => {
              if (bucket.index !== null) onJump(bucket.index);
              else setPending(bucket.label);
            }}
            className={cn(
              "rounded-md px-1.5 text-[length:var(--type-micro)] font-semibold leading-[1.35] tabular transition-colors duration-150",
              inert
                ? "cursor-default text-muted-foreground/25"
                : activeLabel === bucket.label
                  ? "bg-primary/15 text-primary"
                  : waiting
                    ? "animate-pulse text-primary/70"
                    : loaded
                      ? "text-muted-foreground hover:bg-muted/70 hover:text-foreground"
                      : "text-muted-foreground/50 hover:bg-muted/50 hover:text-foreground"
            )}
          >
            {bucket.label}
          </button>
        );
      })}
    </nav>
  );
}

/**
 * The rail's two numbers, from whichever virtualiser is drawing the shelf.
 *
 * The poster grid packs `columns` titles into a virtual row and the list packs
 * one, and that is the only difference between them — so this is written once
 * and both pass their own column count, rather than each working out its own
 * index arithmetic and one of them getting it wrong.
 */
export function useJumpRail(
  virtualizer: Virtualizer<HTMLDivElement, Element>,
  columns: number,
  virtualRows: VirtualItem[]
) {
  const jumpTo = useCallback(
    (index: number) => {
      virtualizer.scrollToIndex(Math.floor(index / Math.max(1, columns)), { align: "start" });
    },
    [columns, virtualizer]
  );

  // A rail is a scrolling aid, so it exists only where there is scrolling to
  // aid. A library of eleven titles fits on one row and drew a 27-letter
  // alphabet twice the height of its own shelf beside it — a control taller
  // than the thing it controls.
  const scrollElement = virtualizer.options.getScrollElement();
  const overflows = scrollElement !== null
    && scrollElement !== undefined
    && virtualizer.getTotalSize() > scrollElement.clientHeight + 1;

  // The first row *on screen*, not the first row rendered.
  //
  // `virtualRows[0]` is three rows above the viewport, because the virtualiser
  // overscans — so reading it as "where you are" marked the rail a screenful
  // behind, and jumping to S lit R. The row on screen is the first whose end
  // is past the scroll offset.
  const offset = virtualizer.scrollOffset ?? 0;
  const firstVisible = virtualRows.find((row) => row.end > offset) ?? virtualRows[0];

  // The *end* of that row — see the comment on `activeLabel`.
  const span = Math.max(1, columns);
  return { show: overflows, activeIndex: ((firstVisible?.index ?? 0) + 1) * span - 1, jumpTo };
}

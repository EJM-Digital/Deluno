import React, { useEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { Star } from "lucide-react";
import type { MediaItem } from "../../lib/media-types";
import { cn, formatBytesFromGb } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { Checkbox } from "../ui/checkbox";
import { PosterArtwork } from "./library-grid";
import { heldQualityLabel } from "../../lib/quality-label";
import { EpisodeProgressBar, TitleMarkLabel, titleBarGradient } from "../ui/title-mark";
import { titleBar } from "../../lib/status-tones";
import { buildJumpBuckets } from "../../lib/library-buckets";
import type { SortField } from "../../lib/library-filters";
import { JumpRail, useJumpRail } from "./library-jump-rail";

export function LibraryTable(
{
  items,
  selectedIds,
  onSelect,
  onToggle,
  onToggleAll,
  allSelected,
  someSelected,
  sortField,
  sortDirection,
  isComplete,
  onEndReached,
  /**
   * Which shelf this is. Only used to decide whether the Episodes column
   * exists: a film has no fraction of itself, so the column would be a
   * dash on every row — the same defect as a filter that can never match.
   */
  variant
}: {
  items: MediaItem[];
  selectedIds: string[];
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
  onToggleAll: () => void;
  allSelected: boolean;
  someSelected: boolean;
  sortField: SortField;
  sortDirection: "asc" | "desc";
  isComplete: boolean;
  onEndReached: () => void;
  variant: "movies" | "shows";
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const tableRef = useRef<HTMLTableElement>(null);
  const [focusIndex, setFocusIndex] = useState(0);
  const rowVirtualizer = useVirtualizer({ count: items.length, getScrollElement: () => scrollRef.current, estimateSize: () => 65, overscan: 10 });
  const virtualRows = rowVirtualizer.getVirtualItems();

  useEffect(() => {
    const lastRow = virtualRows.at(-1);
    if (lastRow && lastRow.index >= items.length - 2) onEndReached();
  }, [items.length, onEndReached, virtualRows]);

  useEffect(() => {
    const el = scrollRef.current;
    const table = tableRef.current;
    if (!el || !table) return;
    function onScroll() {
      if (!el || !table) return;
      if (el.scrollLeft > 2) {
        table.classList.add("is-scrolled");
      } else {
        table.classList.remove("is-scrolled");
      }
    }
    el.addEventListener("scroll", onScroll, { passive: true });
    return () => el.removeEventListener("scroll", onScroll);
  }, []);

  // Keep focus index inside current list bounds whenever items change.
  useEffect(() => {
    if (focusIndex >= items.length) setFocusIndex(Math.max(0, items.length - 1));
  }, [items.length, focusIndex]);

  function focusRow(next: number) {
    const clamped = Math.max(0, Math.min(items.length - 1, next));
    setFocusIndex(clamped);
    const row = tableRef.current?.querySelector<HTMLTableRowElement>(
      `tbody tr[data-row-index="${clamped}"]`
    );
    row?.focus();
    row?.scrollIntoView({ block: "nearest", behavior: "smooth" });
  }

  function handleRowKey(event: React.KeyboardEvent<HTMLTableRowElement>, index: number, item: MediaItem) {
    switch (event.key) {
      case "ArrowDown":
      case "j":
        event.preventDefault();
        focusRow(index + 1);
        break;
      case "ArrowUp":
      case "k":
        event.preventDefault();
        focusRow(index - 1);
        break;
      case "Home":
        event.preventDefault();
        focusRow(0);
        break;
      case "End":
        event.preventDefault();
        focusRow(items.length - 1);
        break;
      case "PageDown":
        event.preventDefault();
        focusRow(index + 10);
        break;
      case "PageUp":
        event.preventDefault();
        focusRow(index - 10);
        break;
      case "Enter":
        event.preventDefault();
        onSelect(item);
        break;
      case "x":
      case " ":
        event.preventDefault();
        onToggle(item.id);
        break;
      default:
        break;
    }
  }

  const buckets = useMemo(
    () => buildJumpBuckets(items, sortField, sortDirection),
    [items, sortDirection, sortField]
  );
  const { slotWidth, activeIndex, jumpTo } = useJumpRail(rowVirtualizer, 1, virtualRows, buckets);

  // The spacer rows the virtualiser needs have to span every column. It was
  // hard-coded to 9, which was right until a column was added — and a spacer
  // one column short does not fail, it quietly skews the layout.
  const columnCount = variant === "shows" ? 10 : 9;

  return (
    <div className="flex items-stretch gap-1">
    <div ref={scrollRef} className="max-h-[calc(100dvh-260px)] min-w-0 flex-1 overflow-auto">
      <table
        ref={tableRef}
        className="data-table min-w-[900px] text-[length:var(--type-body-sm)]"
        role="grid"
        aria-rowcount={items.length}
      >
        <thead>
          <tr>
            <th scope="col" className="col-sticky w-10" style={{ minWidth: 40 }}>
              <TableCheckbox
                checked={allSelected}
                indeterminate={someSelected}
                label="Select all rows"
                onChange={onToggleAll}
              />
            </th>
            <th scope="col" className="col-sticky" style={{ left: 40, minWidth: 280 }}>Title</th>
            <th scope="col" className="hidden md:table-cell">Quality</th>
            <th scope="col">Status</th>
            {/* The same question the poster's bar asks, in words. The list had
                no subtitle state at all, so the two views of one library
                disagreed about what they could tell you (DESIGN-001, #301). */}
            {/*
              Sonarr's Episodes column, which is where its list puts the count
              that its poster wall leaves off. Shows only.
            */}
            {variant === "shows" ? <th scope="col" className="hidden lg:table-cell">Episodes</th> : null}
            <th scope="col" className="hidden lg:table-cell">Subtitles</th>
            <th scope="col" className="hidden lg:table-cell">Genre</th>
            <th scope="col" className="num hidden lg:table-cell">Size</th>
            <th scope="col" className="num hidden md:table-cell">Rating</th>
            <th scope="col" className="hidden xl:table-cell">Added</th>
          </tr>
        </thead>
        <tbody>
          {virtualRows.length > 0 && virtualRows[0].start > 0 ? <tr aria-hidden="true"><td colSpan={columnCount} style={{ height: virtualRows[0].start, padding: 0 }} /></tr> : null}
          {virtualRows.map((virtualRow) => {
            const index = virtualRow.index;
            const item = items[index];
            const isSelected = selectedIds.includes(item.id);
            const isFocused = index === focusIndex;
            return (
              <tr
                key={item.id}
                data-selected={isSelected}
                data-row-index={index}
                tabIndex={isFocused ? 0 : -1}
                aria-selected={isSelected}
                aria-rowindex={index + 1}
                onFocus={() => setFocusIndex(index)}
                onKeyDown={(event) => handleRowKey(event, index, item)}
                className="outline-none focus-visible:shadow-[inset_0_0_0_2px_hsl(var(--primary)/0.7)]"
              >
                <td className="col-sticky" style={{ minWidth: 40 }}>
                  <TableCheckbox
                    checked={isSelected}
                    label="Select row"
                    onChange={() => onToggle(item.id)}
                  />
                </td>
                <td
                  className="col-sticky cursor-pointer"
                  style={{ left: 40, minWidth: 280 }}
                  onClick={() => onSelect(item)}
                >
                  <div className="flex items-center gap-3">
                    <PosterArtwork
                      src={item.poster}
                      title={item.title}
                      className="h-11 w-[30px] shrink-0 rounded-md shadow-card"
                      compact
                    />
                    <div className="min-w-0">
                      {/*
                        No mark here. The row has a Status column, and the mark
                        belongs in it — a dot beside the title and a dot in that
                        column are one fact stated twice, which is what the row
                        already did with "Monitored" while the dot was halved.
                      */}
                      <p className="truncate font-medium text-foreground">{item.title}</p>
                      <p className="text-[length:var(--type-caption)] text-muted-foreground">
                        {item.type === "movie" ? "Movie" : "TV"} · {item.year}
                      </p>
                    </div>
                  </div>
                </td>
                <td className="hidden md:table-cell">
                  {/*
                    What the file is, at the grain the ladder uses — a Remux and
                    a WEB at the same resolution are different files and this
                    column used to call them the same thing.

                    A title with no file gets a dash rather than "Unknown": it is
                    not that the quality is unknown, it is that there is nothing
                    to have a quality. The mark in the next column says why.
                  */}
                  {heldQualityLabel(item)
                    ? <Badge className="whitespace-nowrap">{heldQualityLabel(item)}</Badge>
                    : <span className="text-muted-foreground">—</span>}
                </td>
                <td>
                  {/*
                    **`type`, or this row draws a different red from the poster.**
                    A shelf that has adopted DESIGN-006 paints its marks from the
                    bar SURFACES; without the medium, this label falls back to the
                    page-text palette — measured on the rig as rgb(239,77,77) here
                    against rgb(192,17,28) on the card for the same title.

                    That is the third place this exact mismatch has appeared: the
                    legend chips, this row, and the marks inside them. A list and
                    the shelf it mirrors must not disagree about a colour.
                  */}
                  <TitleMarkLabel item={item} type={variant === "shows" ? "show" : "movie"} />
                </td>
                {variant === "shows" ? (
                  <td className="hidden lg:table-cell">
                    <EpisodeCell item={item} />
                  </td>
                ) : null}
                <td className="hidden lg:table-cell">
                  <SubtitleCell item={item} />
                </td>
                <td className="hidden text-muted-foreground lg:table-cell">
                  {item.genres.slice(0, 2).join(", ")}
                </td>
                <td className="num hidden text-muted-foreground lg:table-cell">
                  {/*
                    Same dash, same reason as Quality: a title with no file has
                    no size, and "Unknown" claims the number exists and could not
                    be read. Ten rows of "Unknown" down a column is a screen
                    saying it does not know things it knows perfectly well.
                  */}
                  {item.hasFile === false ? "—" : formatBytesFromGb(item.sizeGb)}
                </td>
                <td className="num hidden md:table-cell">
                  {/*
                    A filled star beside the word "Unknown" reads as a rating.
                    No rating, no star.
                  */}
                  {item.rating !== null ? (
                    <span className="inline-flex items-center gap-1 text-foreground">
                      <Star className="h-3 w-3 fill-warning text-warning" />
                      {item.rating.toFixed(1)}
                    </span>
                  ) : (
                    <span className="text-muted-foreground">—</span>
                  )}
                </td>
                <td className="hidden text-muted-foreground xl:table-cell">{item.added}</td>
              </tr>
            );
          })}
          {virtualRows.length > 0 && rowVirtualizer.getTotalSize() - virtualRows.at(-1)!.end > 0 ? <tr aria-hidden="true"><td colSpan={columnCount} style={{ height: rowVirtualizer.getTotalSize() - virtualRows.at(-1)!.end, padding: 0 }} /></tr> : null}
        </tbody>
      </table>
    </div>
      {/* Always here, and always this wide — see `useJumpRail`. */}
      <div className="hidden shrink-0 pl-1 sm:block" style={{ width: slotWidth }}>
        <JumpRail buckets={buckets} activeIndex={activeIndex} isComplete={isComplete} onJump={jumpTo} />
      </div>
    </div>
  );
}

/**
 * The subtitle bar, in words.
 *
 * A list row has room for the numbers the poster can only draw, and it is the
 * same measure: languages asked for across the files this title actually has —
 * one for a movie, the episodes you hold for a show.
 *
 * When there is nothing to measure this prints an em dash, exactly as Quality
 * and Size do on the same row. It used to print "Not asked for", which was true
 * of a library that wants no subtitles and a **lie** about every missing title
 * in a library that does: the row asked for two languages and had no file to
 * carry them, and the cell said nobody had asked.
 *
 * James: *"we need to stop using not asked for… if it doesn't have it it's
 * missing plain and simple."* The row already says Missing, in the Status
 * column, once. Saying it again here would be the same defect from the other
 * side.
 */
/**
 * The episodes you hold, the way Sonarr's list draws them.
 *
 * A show Deluno has not yet learned the episode counts for gets a dash, the
 * same as Quality and Size do on this row: it is not that the number is zero,
 * it is that there is no number yet, and printing "0 / 0" claims otherwise.
 */
function EpisodeCell({ item }: { item: MediaItem }) {
  const bar = <EpisodeProgressBar item={item} />;
  return bar ?? <span className="text-muted-foreground">—</span>;
}

function SubtitleCell({ item }: { item: MediaItem }) {
  const bar = titleBar(item);

  if (bar.wanted <= 0) {
    return <span className="text-muted-foreground">—</span>;
  }

  // The poster's own gradient, not a second one written here.
  //
  // This cell used to build its own from `--success` and `--destructive`, which
  // is another place naming the bar's colours by hand and the exact thing
  // `TITLE_BAR_SEGMENTS` exists to stop. It also meant the row and the poster
  // disagreed: the poster paints three rungs and this painted two, so a title
  // whose subtitles were at the cutoff looked identical here to one Deluno was
  // still improving.
  const percent = (value: number) => Math.round(Math.min(1, Math.max(0, value / bar.wanted)) * 100);

  return (
    <span
      className="inline-flex items-center gap-1.5 whitespace-nowrap"
      title={`${bar.held} of ${bar.wanted} ${bar.noun}`}
    >
      <span
        aria-hidden
        className="h-1.5 w-6 shrink-0 rounded-full"
        style={{ background: titleBarGradient(percent(bar.settled), percent(bar.held)) }}
      />
      <span className="tabular">{bar.held} of {bar.wanted}</span>
    </span>
  );
}

function TableCheckbox({ checked, indeterminate, label, onChange }: {
  checked: boolean;
  indeterminate?: boolean;
  label: string;
  onChange: () => void;
}) {
  return (
    <Checkbox
      checked={checked}
      indeterminate={indeterminate}
      onCheckedChange={onChange}
      aria-label={label}
      aria-checked={indeterminate ? "mixed" : checked}
    />
  );
}


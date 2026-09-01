import React, { useEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { GripVertical, Star } from "lucide-react";
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
import {
  listColumnLabel,
  moveListColumn,
  shiftListColumn,
  type LibraryListColumnKey
} from "../../lib/library-list-columns";

const LIST_COLUMN_CLASSES: Record<LibraryListColumnKey, string> = {
  quality: "hidden md:table-cell",
  status: "",
  episodes: "hidden lg:table-cell",
  subtitles: "hidden lg:table-cell",
  genre: "hidden text-muted-foreground lg:table-cell",
  size: "num hidden text-muted-foreground lg:table-cell",
  rating: "num hidden md:table-cell",
  added: "hidden text-muted-foreground xl:table-cell"
};

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
   * Which shelf this is. It validates the Episodes column and keeps the
   * column definitions honest: a film has no fraction of itself, so that
   * column would be a dash on every row — the same defect as a filter that can
   * never match.
   */
  variant,
  columnOrder,
  onColumnOrderChange
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
  /** The user order for the columns after the fixed selection and Title cells. */
  columnOrder: LibraryListColumnKey[];
  onColumnOrderChange: (next: LibraryListColumnKey[]) => void;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const tableRef = useRef<HTMLTableElement>(null);
  const [focusIndex, setFocusIndex] = useState(0);
  const [draggingColumn, setDraggingColumn] = useState<LibraryListColumnKey | null>(null);
  const [dropTargetColumn, setDropTargetColumn] = useState<LibraryListColumnKey | null>(null);
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

  function moveColumn(source: LibraryListColumnKey, target: LibraryListColumnKey) {
    onColumnOrderChange(moveListColumn(columnOrder, source, target));
  }

  function handleColumnDragStart(event: React.DragEvent<HTMLTableCellElement>, column: LibraryListColumnKey) {
    setDraggingColumn(column);
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", column);
  }

  function handleColumnDrop(event: React.DragEvent<HTMLTableCellElement>, target: LibraryListColumnKey) {
    event.preventDefault();
    const source = draggingColumn ?? event.dataTransfer.getData("text/plain") as LibraryListColumnKey;
    if (source) moveColumn(source, target);
    setDraggingColumn(null);
    setDropTargetColumn(null);
  }

  function handleColumnKeyDown(event: React.KeyboardEvent<HTMLTableCellElement>, column: LibraryListColumnKey) {
    if (!event.altKey || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) return;
    event.preventDefault();
    onColumnOrderChange(shiftListColumn(columnOrder, column, event.key === "ArrowLeft" ? -1 : 1));
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

  // The spacer rows the virtualiser needs have to span every column. Keep this
  // derived from the same order that draws the header and cells, so a drag
  // cannot quietly skew the virtualised layout.
  const columnCount = columnOrder.length + 2;

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
            {columnOrder.map((column) => (
              <th
                key={column}
                scope="col"
                data-column-key={column}
                draggable
                tabIndex={0}
                aria-label={`${listColumnLabel(column)} column. Drag to reorder.`}
                aria-grabbed={draggingColumn === column}
                aria-keyshortcuts="Alt+ArrowLeft Alt+ArrowRight"
                title="Drag to reorder. Alt + Left/Right also moves this column."
                onDragStart={(event) => handleColumnDragStart(event, column)}
                onDragOver={(event) => {
                  event.preventDefault();
                  event.dataTransfer.dropEffect = "move";
                  setDropTargetColumn(column);
                }}
                onDrop={(event) => handleColumnDrop(event, column)}
                onDragEnd={() => {
                  setDraggingColumn(null);
                  setDropTargetColumn(null);
                }}
                onKeyDown={(event) => handleColumnKeyDown(event, column)}
                className={cn(
                  LIST_COLUMN_CLASSES[column],
                  "cursor-grab select-none",
                  draggingColumn === column && "opacity-45",
                  dropTargetColumn === column && draggingColumn !== column && "bg-primary/[0.08]"
                )}
              >
                <span className="inline-flex items-center gap-1.5">
                  <GripVertical className="h-3.5 w-3.5 shrink-0 text-muted-foreground/60" aria-hidden="true" />
                  {listColumnLabel(column)}
                </span>
              </th>
            ))}
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
                {columnOrder.map((column) => (
                  <td key={column} data-column-key={column} className={LIST_COLUMN_CLASSES[column]}>
                    {renderListCell(column, item, variant)}
                  </td>
                ))}
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

function renderListCell(column: LibraryListColumnKey, item: MediaItem, variant: "movies" | "shows") {
  switch (column) {
    case "quality": {
      const quality = heldQualityLabel(item);
      return quality
        ? <Badge className="whitespace-nowrap">{quality}</Badge>
        : <span className="text-muted-foreground">—</span>;
    }
    case "status":
      /* `type` keeps the row on the same surface palette as its poster. */
      return <TitleMarkLabel item={item} type={variant === "shows" ? "show" : "movie"} />;
    case "episodes":
      return <EpisodeCell item={item} />;
    case "subtitles":
      return <SubtitleCell item={item} />;
    case "genre":
      return item.genres.slice(0, 2).join(", ");
    case "size":
      return item.hasFile === false ? "—" : formatBytesFromGb(item.sizeGb);
    case "rating":
      return item.rating !== null ? (
        <span className="inline-flex items-center gap-1 text-foreground">
          <Star className="h-3 w-3 fill-warning text-warning" />
          {item.rating.toFixed(1)}
        </span>
      ) : (
        <span className="text-muted-foreground">—</span>
      );
    case "added":
      return item.added;
    default:
      return null;
  }
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
  return typeof item.airedEpisodeCount === "number" && item.airedEpisodeCount > 0
    ? <EpisodeProgressBar item={item} type="show" />
    : <span className="text-muted-foreground">—</span>;
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


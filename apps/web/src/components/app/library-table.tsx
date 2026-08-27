import React, { useEffect, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { Star } from "lucide-react";
import type { MediaItem } from "../../lib/media-types";
import { cn, formatBytesFromGb } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { Checkbox } from "../ui/checkbox";
import { PosterArtwork, shortQuality } from "./library-grid";
import { TitleMarkLabel } from "../ui/title-mark";
import { titleBar } from "../../lib/status-tones";

export function LibraryTable(
{
  items,
  selectedIds,
  onSelect,
  onToggle,
  onToggleAll,
  allSelected,
  someSelected,
  onEndReached
}: {
  items: MediaItem[];
  selectedIds: string[];
  onSelect: (item: MediaItem) => void;
  onToggle: (id: string) => void;
  onToggleAll: () => void;
  allSelected: boolean;
  someSelected: boolean;
  onEndReached: () => void;
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

  return (
    <div ref={scrollRef} className="max-h-[calc(100dvh-260px)] overflow-auto">
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
            <th scope="col" className="hidden lg:table-cell">Subtitles</th>
            <th scope="col" className="hidden lg:table-cell">Genre</th>
            <th scope="col" className="num hidden lg:table-cell">Size</th>
            <th scope="col" className="num hidden md:table-cell">Rating</th>
            <th scope="col" className="hidden xl:table-cell">Added</th>
          </tr>
        </thead>
        <tbody>
          {virtualRows.length > 0 && virtualRows[0].start > 0 ? <tr aria-hidden="true"><td colSpan={9} style={{ height: virtualRows[0].start, padding: 0 }} /></tr> : null}
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
                  <Badge>{item.quality ? shortQuality(item.quality) : "Unknown"}</Badge>
                </td>
                <td>
                  <TitleMarkLabel item={item} />
                </td>
                <td className="hidden lg:table-cell">
                  <SubtitleCell item={item} />
                </td>
                <td className="hidden text-muted-foreground lg:table-cell">
                  {item.genres.slice(0, 2).join(", ")}
                </td>
                <td className="num hidden text-muted-foreground lg:table-cell">
                  {formatBytesFromGb(item.sizeGb)}
                </td>
                <td className="num hidden md:table-cell">
                  <span className="inline-flex items-center gap-1 text-foreground">
                    <Star className="h-3 w-3 fill-warning text-warning" />
                    {item.rating !== null ? item.rating.toFixed(1) : "Unknown"}
                  </span>
                </td>
                <td className="hidden text-muted-foreground xl:table-cell">{item.added}</td>
              </tr>
            );
          })}
          {virtualRows.length > 0 && rowVirtualizer.getTotalSize() - virtualRows.at(-1)!.end > 0 ? <tr aria-hidden="true"><td colSpan={9} style={{ height: rowVirtualizer.getTotalSize() - virtualRows.at(-1)!.end, padding: 0 }} /></tr> : null}
        </tbody>
      </table>
    </div>
  );
}

/**
 * The subtitle bar, in words.
 *
 * A list row has room for the numbers the poster can only draw, and it is the
 * same measure: languages asked for across the files this title actually has —
 * one for a movie, the episodes you hold for a show. A title that asked for
 * nothing says so rather than showing "0 of 0".
 */
function SubtitleCell({ item }: { item: MediaItem }) {
  const bar = titleBar(item);

  if (bar.wanted <= 0) {
    return <span className="text-muted-foreground">Not asked for</span>;
  }

  const complete = bar.held >= bar.wanted;
  return (
    <span
      className="inline-flex items-center gap-1.5 whitespace-nowrap"
      title={`${bar.held} of ${bar.wanted} ${bar.noun}`}
    >
      <span
        aria-hidden
        className={cn("h-1.5 w-6 shrink-0 rounded-full", complete ? "bg-success" : "bg-mark-idle")}
        style={complete ? undefined : {
          background: `linear-gradient(to right, hsl(var(--success)) 0 ${Math.round((bar.held / bar.wanted) * 100)}%, hsl(var(--destructive)) ${Math.round((bar.held / bar.wanted) * 100)}% 100%)`
        }}
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


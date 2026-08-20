import React, { useEffect, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { Star } from "lucide-react";
import type { MediaItem } from "../../lib/media-types";
import { formatBytesFromGb } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { Checkbox } from "../ui/checkbox";
import { PosterArtwork, shortQuality, StatusBadge, StatusDot } from "./library-grid";

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
        className="data-table min-w-[900px] text-[13px]"
        role="grid"
        aria-rowcount={items.length}
      >
        <thead>
          <tr>
            <th className="col-sticky w-10" style={{ minWidth: 40 }}>
              <TableCheckbox
                checked={allSelected}
                indeterminate={someSelected}
                onChange={onToggleAll}
              />
            </th>
            <th className="col-sticky" style={{ left: 40, minWidth: 280 }}>Title</th>
            <th className="hidden md:table-cell">Quality</th>
            <th>Status</th>
            <th className="hidden lg:table-cell">Genre</th>
            <th className="num hidden lg:table-cell">Size</th>
            <th className="num hidden md:table-cell">Rating</th>
            <th className="hidden xl:table-cell">Added</th>
          </tr>
        </thead>
        <tbody>
          {virtualRows.length > 0 && virtualRows[0].start > 0 ? <tr aria-hidden="true"><td colSpan={8} style={{ height: virtualRows[0].start, padding: 0 }} /></tr> : null}
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
                      <div className="flex items-center gap-2">
                        <StatusDot status={item.status} />
                        <p className="truncate font-medium text-foreground">{item.title}</p>
                      </div>
                      <p className="text-[11px] text-muted-foreground">
                        {item.type === "movie" ? "Movie" : "TV"} · {item.year}
                        {item.monitored ? " · Monitored" : " · Not monitored"}
                      </p>
                    </div>
                  </div>
                </td>
                <td className="hidden md:table-cell">
                  <Badge>{item.quality ? shortQuality(item.quality) : "Unknown"}</Badge>
                </td>
                <td>
                  <StatusBadge status={item.status} />
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
          {virtualRows.length > 0 && rowVirtualizer.getTotalSize() - virtualRows.at(-1)!.end > 0 ? <tr aria-hidden="true"><td colSpan={8} style={{ height: rowVirtualizer.getTotalSize() - virtualRows.at(-1)!.end, padding: 0 }} /></tr> : null}
        </tbody>
      </table>
    </div>
  );
}

function TableCheckbox({ checked, indeterminate, onChange }: {
  checked: boolean;
  indeterminate?: boolean;
  onChange: () => void;
}) {
  return <Checkbox checked={checked} indeterminate={indeterminate} onCheckedChange={onChange} aria-label={checked ? "Deselect" : "Select"} />;
}


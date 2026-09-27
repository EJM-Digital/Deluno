/**
 * Sort, filter, and bulk-select for the indexer ListTable (#737). Prowlarr's
 * indexer table sorts by column, filters by protocol/privacy/status/scope, and
 * lets several indexers be changed or removed together instead of one drawer
 * edit at a time; this gives Deluno the same three things.
 */
import type { ReactNode } from "react";
import { ChevronDown, ChevronUp, X } from "lucide-react";
import type { IndexerItem } from "../../lib/api";
import { Button } from "../../components/ui/button";
import { Checkbox } from "../../components/ui/checkbox";
import { Select } from "../../components/ui/select";
import { cn } from "../../lib/utils";
import { healthChip, protocolLabel, scopeLabel } from "./format";

export type IndexerSortKey = "name" | "protocol" | "usedFor" | "lastTest" | "status";
export type SortDirection = "asc" | "desc";
export interface IndexerSortState {
  key: IndexerSortKey;
  direction: SortDirection;
}

function indexerSortValue(item: IndexerItem, key: IndexerSortKey): string | number {
  switch (key) {
    case "name":
      return item.name.toLowerCase();
    case "protocol":
      return protocolLabel(item.protocol).toLowerCase();
    case "usedFor":
      return scopeLabel(item.mediaScope).toLowerCase();
    case "lastTest":
      return item.lastHealthTestUtc ? new Date(item.lastHealthTestUtc).getTime() : Number.NEGATIVE_INFINITY;
    case "status":
      // Disabled sorts apart from enabled, then alphabetically by the same
      // label the Status chip shows, so the order matches what's on screen.
      return `${item.isEnabled ? 0 : 1}-${healthChip(item).label.toLowerCase()}`;
  }
}

/** Stable sort: equal keys keep their existing relative order. */
export function sortIndexers(items: IndexerItem[], sort: IndexerSortState | null): IndexerItem[] {
  if (!sort) return items;
  const { key, direction } = sort;
  const decorated = items.map((item, index) => ({ item, index, value: indexerSortValue(item, key) }));
  decorated.sort((a, b) => {
    if (a.value < b.value) return -1;
    if (a.value > b.value) return 1;
    return a.index - b.index;
  });
  const ordered = decorated.map((entry) => entry.item);
  return direction === "asc" ? ordered : ordered.reverse();
}

export function nextIndexerSort(current: IndexerSortState | null, key: IndexerSortKey): IndexerSortState {
  if (current?.key === key) return { key, direction: current.direction === "asc" ? "desc" : "asc" };
  return { key, direction: "asc" };
}

export interface IndexerFilters {
  protocol: string;
  privacy: string;
  status: "all" | "enabled" | "disabled";
  scope: string;
}

export function emptyIndexerFilters(): IndexerFilters {
  return { protocol: "", privacy: "", status: "all", scope: "" };
}

export function indexerFiltersActive(filters: IndexerFilters): boolean {
  return Boolean(filters.protocol || filters.privacy || filters.scope) || filters.status !== "all";
}

export function filterIndexers(items: IndexerItem[], filters: IndexerFilters): IndexerItem[] {
  return items.filter((item) => {
    if (filters.protocol && item.protocol !== filters.protocol) return false;
    if (filters.privacy && item.privacy !== filters.privacy) return false;
    if (filters.status === "enabled" && !item.isEnabled) return false;
    if (filters.status === "disabled" && item.isEnabled) return false;
    if (filters.scope && (item.mediaScope ?? "both") !== filters.scope) return false;
    return true;
  });
}

/** Clickable column header — sortable columns pass one as their `label`. */
export function IndexerSortHeader({ label, sortKey, sort, onSort }: { label: ReactNode; sortKey: IndexerSortKey; sort: IndexerSortState | null; onSort: (key: IndexerSortKey) => void }) {
  const active = sort?.key === sortKey;
  return (
    <button
      type="button"
      onClick={() => onSort(sortKey)}
      className={cn("flex items-center gap-1 truncate uppercase tracking-[0.1em] hover:text-foreground", active && "text-foreground")}
    >
      {label}
      {active ? (sort?.direction === "asc" ? <ChevronUp className="h-3 w-3" /> : <ChevronDown className="h-3 w-3" />) : null}
    </button>
  );
}

function distinctValues(items: IndexerItem[], pick: (item: IndexerItem) => string) {
  return [...new Set(items.map(pick).filter(Boolean))].sort();
}

const STATUS_OPTIONS = [
  { value: "all", label: "Any status" },
  { value: "enabled", label: "Enabled" },
  { value: "disabled", label: "Disabled" }
];
const SCOPE_OPTIONS = [
  { value: "", label: "Any scope" },
  { value: "movies", label: "Movies" },
  { value: "tv", label: "TV" },
  { value: "both", label: "Movies · TV" }
];

export function IndexerFilterBar({ indexers, filters, onChange }: { indexers: IndexerItem[]; filters: IndexerFilters; onChange: (filters: IndexerFilters) => void }) {
  const protocolOptions = distinctValues(indexers, (item) => item.protocol);
  const privacyOptions = distinctValues(indexers, (item) => item.privacy);
  return (
    <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Filter indexers">
      <Select
        aria-label="Filter by protocol"
        chrome
        className="h-[var(--control-height-sm)] w-auto min-w-[7rem] text-[length:var(--type-caption)]"
        value={filters.protocol}
        onChange={(event) => onChange({ ...filters, protocol: event.target.value })}
        placeholder="Any protocol"
        options={protocolOptions.map((value) => ({ value, label: protocolLabel(value) }))}
      />
      {privacyOptions.length > 1 ? (
        <Select
          aria-label="Filter by privacy"
          chrome
          className="h-[var(--control-height-sm)] w-auto min-w-[6rem] text-[length:var(--type-caption)]"
          value={filters.privacy}
          onChange={(event) => onChange({ ...filters, privacy: event.target.value })}
          placeholder="Any privacy"
          options={privacyOptions.map((value) => ({ value, label: value }))}
        />
      ) : null}
      <Select
        aria-label="Filter by status"
        chrome
        className="h-[var(--control-height-sm)] w-auto min-w-[7rem] text-[length:var(--type-caption)]"
        value={filters.status}
        onChange={(event) => onChange({ ...filters, status: event.target.value as IndexerFilters["status"] })}
        options={STATUS_OPTIONS}
      />
      <Select
        aria-label="Filter by media scope"
        chrome
        className="h-[var(--control-height-sm)] w-auto min-w-[7rem] text-[length:var(--type-caption)]"
        value={filters.scope}
        onChange={(event) => onChange({ ...filters, scope: event.target.value })}
        options={SCOPE_OPTIONS}
      />
      {indexerFiltersActive(filters) ? (
        <Button type="button" variant="ghost" size="sm" onClick={() => onChange(emptyIndexerFilters())}>
          <X className="h-3.5 w-3.5" />
          Clear filters
        </Button>
      ) : null}
    </div>
  );
}

export function IndexerSelectAllCheckbox({ items, selected, onChange }: { items: IndexerItem[]; selected: Set<string>; onChange: (ids: string[]) => void }) {
  const allSelected = items.length > 0 && items.every((item) => selected.has(item.id));
  const someSelected = !allSelected && items.some((item) => selected.has(item.id));
  return (
    <Checkbox
      aria-label={allSelected ? "Deselect all indexers" : "Select all indexers"}
      checked={allSelected}
      indeterminate={someSelected}
      onCheckedChange={(checked) => onChange(checked ? items.map((item) => item.id) : [])}
    />
  );
}

export type IndexerBulkScope = "movies" | "tv" | "both";

export function IndexerBulkBar({
  count,
  busy,
  onEnable,
  onPause,
  onRemove,
  onChangeScope,
  onClear
}: {
  count: number;
  busy: boolean;
  onEnable: () => void;
  onPause: () => void;
  onRemove: () => void;
  onChangeScope: (scope: IndexerBulkScope) => void;
  onClear: () => void;
}) {
  if (count === 0) return null;
  return (
    <div role="toolbar" aria-label="Bulk indexer actions" className="flex flex-wrap items-center gap-2 border-b border-hairline bg-surface-2/40 px-[var(--card-pad-x)] py-2">
      <span className="text-[length:var(--type-caption)] font-medium text-foreground">{count} selected</span>
      <Button type="button" variant="outline" size="sm" disabled={busy} onClick={onEnable}>Enable</Button>
      <Button type="button" variant="outline" size="sm" disabled={busy} onClick={onPause}>Pause</Button>
      <Select
        aria-label="Change used-for scope for selected indexers"
        chrome
        className="h-[var(--control-height-sm)] w-auto text-[length:var(--type-caption)]"
        value=""
        disabled={busy}
        onChange={(event) => {
          const value = event.target.value as IndexerBulkScope;
          if (value) onChangeScope(value);
        }}
        placeholder="Used for…"
        options={[{ value: "movies", label: "Movies" }, { value: "tv", label: "TV" }, { value: "both", label: "Movies · TV" }]}
      />
      <Button type="button" variant="destructive" size="sm" disabled={busy} onClick={onRemove}>Remove</Button>
      <span className="flex-1" />
      <Button type="button" variant="ghost" size="sm" onClick={onClear}>Clear selection</Button>
    </div>
  );
}

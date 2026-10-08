/**
 * Sort, filter, and bulk-select for the indexer ListTable (#737). Prowlarr's
 * indexer table sorts by column, filters by protocol/privacy/status/scope, and
 * lets several indexers be changed or removed together instead of one drawer
 * edit at a time; this gives Deluno the same three things.
 */
import { FilterChips, MoreFilters } from "../../components/ui/filter-chips";
import type { ReactNode } from "react";
import { X } from "lucide-react";
import type { IndexerItem } from "../../lib/api";
import { Button } from "../../components/ui/button";
import { Checkbox } from "../../components/ui/checkbox";
import { Select } from "../../components/ui/select";
import { cn } from "../../lib/utils";
import { healthChip, protocolLabel, scopeLabel } from "./format";

export type IndexerSortKey = "name" | "protocol" | "usedFor" | "lastTest" | "status";

/** What each sortable column of the indexer list sorts by (the list's own column headers do the clicking, #1119). */
export function indexerSortValue(item: IndexerItem, key: IndexerSortKey): string | number {
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

function scopeIncludes(mediaScope: string | null | undefined, scope: string): boolean {
  const own = mediaScope ?? "both";
  return own === scope || own === "both";
}

export function filterIndexers(items: IndexerItem[], filters: IndexerFilters): IndexerItem[] {
  return items.filter((item) => {
    if (filters.protocol && item.protocol !== filters.protocol) return false;
    if (filters.privacy && item.privacy !== filters.privacy) return false;
    if (filters.status === "enabled" && !item.isEnabled) return false;
    if (filters.status === "disabled" && item.isEnabled) return false;
    // Movies or TV means "searched for it": an indexer used for both counts under each.
    if (filters.scope && !scopeIncludes(item.mediaScope, filters.scope)) return false;
    return true;
  });
}

function distinctValues(items: IndexerItem[], pick: (item: IndexerItem) => string) {
  return [...new Set(items.map(pick).filter(Boolean))].sort();
}

/**
 * On / Off and Movies · TV, as chips with counts in the Indexers card's header: the way every list filters
 * (the owner, 2 Oct 2026). Protocol and privacy, rarer choices, sit on the line below in `IndexerFilterBar`.
 */
export function IndexerFilterChips({ indexers, filters, onChange }: { indexers: IndexerItem[]; filters: IndexerFilters; onChange: (filters: IndexerFilters) => void }) {
  const on = indexers.filter((item) => item.isEnabled).length;
  const movies = indexers.filter((item) => scopeIncludes(item.mediaScope, "movies")).length;
  const tv = indexers.filter((item) => scopeIncludes(item.mediaScope, "tv")).length;
  return (
    <div className="flex min-w-0 items-center gap-3">
      <FilterChips<IndexerFilters["status"]>
        label="Filter by on or off"
        size="sm"
        value={filters.status}
        onValueChange={(status) => onChange({ ...filters, status })}
        options={[
          { value: "all", label: "All", count: indexers.length },
          { value: "enabled", label: "On", count: on },
          { value: "disabled", label: "Off", count: indexers.length - on }
        ]}
      />
      {movies !== indexers.length || tv !== indexers.length ? (
        <FilterChips<"" | "movies" | "tv">
          label="Filter by what it searches for"
          size="sm"
          value={filters.scope === "movies" || filters.scope === "tv" ? filters.scope : ""}
          onValueChange={(scope) => onChange({ ...filters, scope })}
          options={[
            { value: "", label: "Movies · TV", count: indexers.length },
            { value: "movies", label: "Movies", count: movies },
            { value: "tv", label: "TV", count: tv }
          ]}
        />
      ) : null}
    </div>
  );
}

/**
 * The rarer filters, protocol and privacy, as compact pickers after the chips in the card's header, as every card's
 * filters sit (the owner, 3 Oct 2026: a row of their own under the header was one control on a line by itself); nothing
 * when neither narrows.
 */
export function IndexerFilterBar({ indexers, filters, onChange }: { indexers: IndexerItem[]; filters: IndexerFilters; onChange: (filters: IndexerFilters) => void }) {
  const protocolOptions = distinctValues(indexers, (item) => item.protocol);
  const privacyOptions = distinctValues(indexers, (item) => item.privacy);
  const active = indexerFiltersActive(filters);
  if (protocolOptions.length < 2 && privacyOptions.length < 2 && !active) return null;
  return (
    <div className="flex shrink-0 items-center gap-2" role="group" aria-label="Filter indexers">
      {protocolOptions.length > 1 || privacyOptions.length > 1 ? (
      <MoreFilters
        groups={[
          ...(protocolOptions.length > 1
            ? [{ label: "Protocol", ariaLabel: "Filter by protocol", value: filters.protocol, onValueChange: (protocol: string) => onChange({ ...filters, protocol }), options: [{ value: "", label: "Any protocol" }, ...protocolOptions.map((value) => ({ value, label: protocolLabel(value) }))] }]
            : []),
          ...(privacyOptions.length > 1
            ? [{ label: "Privacy", ariaLabel: "Filter by privacy", value: filters.privacy, onValueChange: (privacy: string) => onChange({ ...filters, privacy }), options: [{ value: "", label: "Any privacy" }, ...privacyOptions.map((value) => ({ value, label: value }))] }]
            : [])
        ]}
      />
      ) : null}
      {active ? (
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
  onTags,
  onClear
}: {
  count: number;
  busy: boolean;
  onEnable: () => void;
  onPause: () => void;
  onRemove: () => void;
  onChangeScope: (scope: IndexerBulkScope) => void;
  onTags?: () => void;
  onClear: () => void;
}) {
  if (count === 0) return null;
  return (
    <div role="toolbar" aria-label="Bulk indexer actions" className="flex flex-wrap items-center gap-2 border-b border-hairline bg-surface-2/40 px-(--card-pad-x) py-2">
      <span className="text-(length:--type-caption) font-medium text-foreground">{count} selected</span>
      <Button type="button" variant="outline" size="sm" disabled={busy} onClick={onEnable}>Enable</Button>
      <Button type="button" variant="outline" size="sm" disabled={busy} onClick={onPause}>Pause</Button>
      <Select
        aria-label="Change used-for scope for selected indexers"
        chrome
        className="h-(--control-height-sm) w-auto text-(length:--type-caption)"
        value=""
        disabled={busy}
        onChange={(event) => {
          const value = event.target.value as IndexerBulkScope;
          if (value) onChangeScope(value);
        }}
        placeholder="Used for…"
        options={[{ value: "movies", label: "Movies" }, { value: "tv", label: "TV" }, { value: "both", label: "Movies · TV" }]}
      />
      {onTags ? <Button type="button" variant="outline" size="sm" aria-label="Change tags on the selected indexers" disabled={busy} onClick={onTags}>Tags</Button> : null}
      <Button type="button" variant="destructive" size="sm" disabled={busy} onClick={onRemove}>Remove</Button>
      <span className="flex-1" />
      <Button type="button" variant="ghost" size="sm" onClick={onClear}>Clear selection</Button>
    </div>
  );
}

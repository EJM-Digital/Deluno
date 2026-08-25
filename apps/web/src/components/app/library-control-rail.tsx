import {
  ArrowDownAZ, ArrowUpDown, ChevronDown, Filter, LayoutGrid, LayoutTemplate, List, Search, X
} from "lucide-react";
import React, { useLayoutEffect, useRef, useState } from "react";
import type { CatalogueFacets } from "../../lib/api";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Select } from "../ui/select";
import { SwitchRow } from "../ui/switch";
import type { CardSize, DisplayOptions } from "./library-grid";

export type QuickFilter = "all" | "monitored" | "unmonitored" | "downloaded" | "missing" | "upgrades";
export type ViewMode = "grid" | "list";
export type SortField = "title" | "year" | "rating" | "added";
export type SortDirection = "asc" | "desc";
export interface SavedFilterPreset {
  id: string;
  name: string;
  libraryId: string | null;
  quickFilter: QuickFilter;
  sortField: SortField;
  sortDirection: SortDirection;
  viewMode: ViewMode;
  cardSize: CardSize;
  displayOptions: DisplayOptions;
}

export const quickFilterConfig: Array<{ key: QuickFilter; label: string }> = [
  { key: "all", label: "All" },
  { key: "monitored", label: "Monitored" },
  { key: "unmonitored", label: "Unmonitored" },
  { key: "downloaded", label: "Downloaded" },
  { key: "missing", label: "Missing" },
  { key: "upgrades", label: "Upgrades" }
];

export const sortFieldOptions: Array<{ value: SortField; label: string }> = [
  { value: "title", label: "Title" },
  { value: "year", label: "Year" },
  { value: "rating", label: "Rating" },
  { value: "added", label: "Added" }
];

export function isQuickFilter(value: string | null): value is QuickFilter {
  return quickFilterConfig.some((filter) => filter.key === value);
}

export function isSortField(value: string | null): value is SortField {
  return sortFieldOptions.some((sort) => sort.value === value);
}

export interface LibraryControls {
  query: string;
  setQuery: (value: string) => void;
  libraryId: string | null;
  setLibraryId: (value: string | null) => void;
  libraries: Array<{ id: string; name: string }>;
  quickFilter: QuickFilter;
  setQuickFilter: (value: QuickFilter) => void;
  sortField: SortField;
  setSortField: (value: SortField) => void;
  sortDirection: SortDirection;
  setSortDirection: (value: SortDirection) => void;
  view: ViewMode;
  setView: (value: ViewMode) => void;
  cardSize: CardSize;
  changeSize: (value: CardSize) => void;
  displayOptions: DisplayOptions;
  setDisplayOptions: (value: DisplayOptions) => void;
  savedPresets: SavedFilterPreset[];
  newPresetName: string;
  setNewPresetName: (value: string) => void;
  isSavingPreset: boolean;
  saveCurrentPreset: () => void | Promise<void>;
  applyPreset: (preset: SavedFilterPreset) => void;
  deletePreset: (presetId: string) => void;
  activeFilterCount: number;
}

export function ControlRail({ label, facets, controls }: {
  label: string;
  facets: CatalogueFacets | null;
  controls: LibraryControls;
}) {
  const {
    query, setQuery, libraryId, setLibraryId, libraries, quickFilter, setQuickFilter, sortField, setSortField,
    sortDirection, setSortDirection, view, setView, cardSize, changeSize,
    displayOptions, setDisplayOptions, savedPresets, newPresetName,
    setNewPresetName, isSavingPreset, saveCurrentPreset, applyPreset,
    deletePreset, activeFilterCount
  } = controls;
  const [openPanel, setOpenPanel] = useState<"view" | "sort" | "filter" | null>(null);
  const pillTrackRef = useRef<HTMLDivElement>(null);
  const btnRefs = useRef<Map<QuickFilter, HTMLButtonElement | null>>(new Map());
  const [pill, setPill] = useState({ left: 0, width: 0, ready: false });

  useLayoutEffect(() => {
    const track = pillTrackRef.current;
    const btn = btnRefs.current.get(quickFilter);
    if (!track || !btn) return;
    const tRect = track.getBoundingClientRect();
    const bRect = btn.getBoundingClientRect();
    setPill({ left: bRect.left - tRect.left, width: bRect.width, ready: true });
  }, [quickFilter]);

  const counts: Record<QuickFilter, number> = {
    all: facets?.all ?? 0,
    monitored: facets?.monitored ?? 0,
    unmonitored: facets?.unmonitored ?? 0,
    downloaded: facets?.downloaded ?? 0,
    missing: facets?.missing ?? 0,
    upgrades: facets?.upgrades ?? 0
  };

  return (
    <div className="sticky top-[var(--topbar-height-mobile)] z-20 lg:top-topbar">
      <div
        className={cn(
          "relative overflow-hidden rounded-2xl",
          "border border-hairline/60 dark:border-white/[0.07]",
          "bg-background/80 backdrop-blur-2xl supports-[backdrop-filter]:bg-background/72",
          "dark:bg-[hsl(226_24%_7%/0.88)]",
          "shadow-[0_2px_16px_hsl(0_0%_0%/0.06),0_1px_3px_hsl(0_0%_0%/0.04)]",
          "dark:shadow-[0_4px_24px_hsl(0_0%_0%/0.28),0_1px_4px_hsl(0_0%_0%/0.2)]"
        )}
      >
        <div
          aria-hidden
          className="pointer-events-none absolute inset-x-0 top-0 h-px opacity-60"
          style={{ background: "linear-gradient(90deg, transparent 5%, hsl(var(--primary)/0.4) 35%, hsl(var(--primary-2)/0.4) 65%, transparent 95%)" }}
        />

        <div className="px-[calc(var(--tile-pad)*0.8)] py-[calc(var(--tile-pad)*0.65)]">
          <div className="flex flex-wrap items-center gap-2">
            <div className={cn(
              "group relative flex min-w-[240px] flex-1 items-center gap-2.5 rounded-xl px-3.5 transition-all duration-200",
              "min-h-[var(--library-toolbar-height)] bg-foreground/[0.04] dark:bg-white/[0.05]",
              "ring-1 ring-inset ring-hairline/60 dark:ring-white/[0.06]",
              "focus-within:bg-foreground/[0.06] focus-within:ring-primary/35",
              "focus-within:shadow-[0_0_0_3px_hsl(var(--primary)/0.09)]"
            )}>
              <Search className="h-3.5 w-3.5 shrink-0 text-muted-foreground/50 transition-colors duration-200 group-focus-within:text-primary/70" strokeWidth={2} />
              <Input
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder={`Search ${label}…`}
                className="h-full border-0 bg-transparent px-0 text-[length:var(--library-toolbar-size)] shadow-none placeholder:text-muted-foreground/40 focus-visible:ring-0"
              />
              {query ? (
                <button
                  type="button"
                  onClick={() => setQuery("")}
                  aria-label="Clear"
                  className="flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-muted-foreground/20 text-muted-foreground transition hover:bg-foreground/15 hover:text-foreground"
                >
                  <span className="text-[length:var(--type-micro)] font-bold leading-none">×</span>
                </button>
              ) : (
                <kbd className="hidden shrink-0 rounded border border-hairline/70 bg-background/50 px-1.5 py-px font-mono text-[length:var(--library-badge-size)] text-muted-foreground/40 group-focus-within:hidden sm:block">
                  /
                </kbd>
              )}
            </div>

            <label className="flex min-h-[var(--library-toolbar-height)] min-w-[11rem] items-center rounded-xl bg-foreground/[0.04] px-2.5 ring-1 ring-inset ring-hairline/60 dark:bg-white/[0.05] dark:ring-white/[0.06]">
              <span className="sr-only">Library</span>
              <Select
                aria-label="Library"
                value={libraryId ?? ""}
                onChange={(event) => setLibraryId(event.target.value || null)}
                className="h-[calc(var(--library-toolbar-height)-0.5rem)] border-0 bg-transparent px-1 text-[length:var(--library-toolbar-size)] font-semibold shadow-none focus-visible:ring-0"
                options={[
                  { value: "", label: "All libraries" },
                  ...libraries.map((library) => ({ value: library.id, label: library.name }))
                ]}
              />
            </label>

            <ToolbarMenuButton
              label="Display"
              icon={LayoutTemplate}
              active={openPanel === "view"}
              meta={view === "grid" ? `Poster grid · ${cardSize === "sm" ? "Small" : cardSize === "lg" ? "Large" : "Medium"}` : "Compact list"}
              onClick={() => setOpenPanel((current) => current === "view" ? null : "view")}
            />
            <ToolbarMenuButton
              label="Order"
              icon={ArrowUpDown}
              active={openPanel === "sort"}
              meta={`${sortFieldOptions.find((option) => option.value === sortField)?.label ?? "Title"} · ${sortDirection === "asc" ? "A–Z" : "Z–A"}`}
              onClick={() => setOpenPanel((current) => current === "sort" ? null : "sort")}
            />
            <ToolbarMenuButton
              label="Refine"
              icon={Filter}
              active={openPanel === "filter"}
              meta={activeFilterCount > 0 ? `${activeFilterCount} active` : "Quick filters"}
              onClick={() => setOpenPanel((current) => current === "filter" ? null : "filter")}
            />
          </div>

          <div className="mt-2.5">
            <div ref={pillTrackRef} className="relative flex flex-wrap items-center gap-0.5">
              {pill.ready ? (
                <div
                  aria-hidden
                  className="absolute rounded-lg bg-foreground/[0.07] dark:bg-white/[0.09]"
                  style={{
                    left: pill.left,
                    width: pill.width,
                    height: "calc(var(--library-toolbar-height) * 0.74)",
                    top: "50%",
                    transform: "translateY(-50%)",
                    transition: "left 0.22s cubic-bezier(0.4,0,0.2,1), width 0.22s cubic-bezier(0.4,0,0.2,1)"
                  }}
                />
              ) : null}

              {quickFilterConfig.map((chip) => {
                const active = quickFilter === chip.key;
                return (
                  <button
                    key={chip.key}
                    ref={(element) => { btnRefs.current.set(chip.key, element); }}
                    type="button"
                    onClick={() => setQuickFilter(chip.key)}
                    className={cn(
                      "relative flex min-h-[calc(var(--library-toolbar-height)*0.78)] items-center gap-1.5 rounded-lg px-3 text-[length:var(--library-toolbar-size)] select-none",
                      active ? "font-semibold text-foreground" : "font-medium text-muted-foreground hover:text-foreground"
                    )}
                  >
                    <span>{chip.label}</span>
                    <span
                      className={cn(
                        "tabular rounded-md px-1.5 py-px text-[length:var(--library-badge-size)] font-bold leading-tight",
                        active ? "bg-primary/15 text-primary dark:bg-primary/20" : "bg-foreground/[0.06] text-muted-foreground dark:bg-white/[0.07]"
                      )}
                    >
                      {counts[chip.key]}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>

          {openPanel === "view" ? (
            <div className="mt-3 overflow-hidden rounded-2xl border border-hairline bg-surface-1">
              <LibraryControlPanelHeader
                icon={LayoutTemplate}
                eyebrow="Display"
                title="Choose how your library feels"
                description="Start with a visual poster grid or a dense list. Your choice is remembered separately for movies and TV."
                onClose={() => setOpenPanel(null)}
              />
              <div className="grid gap-[var(--grid-gap)] p-[calc(var(--tile-pad)*0.8)] xl:grid-cols-[minmax(0,1.05fr)_minmax(320px,0.95fr)]">
                <div className="space-y-[var(--grid-gap)]">
                  <div>
                    <SectionLabel>Layout</SectionLabel>
                    <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">Pick the view that best fits the job in front of you.</p>
                  </div>
                  <div className="grid gap-2 sm:grid-cols-2">
                    <LibraryViewChoice mode="grid" label="Poster grid" description="Artwork-led browsing for your collection." selected={view === "grid"} onClick={() => setView("grid")} />
                    <LibraryViewChoice mode="list" label="Compact list" description="More titles and file details in less space." selected={view === "list"} onClick={() => setView("list")} />
                  </div>

                  {view === "grid" ? (
                    <div className="rounded-xl border border-hairline bg-background/45 p-3">
                      <SectionLabel>Poster size</SectionLabel>
                      <div className="mt-2 grid grid-cols-3 gap-2">
                        {(["sm", "md", "lg"] as CardSize[]).map((size) => (
                          <PosterSizeChoice key={size} size={size} selected={cardSize === size} onClick={() => changeSize(size)} />
                        ))}
                      </div>
                    </div>
                  ) : null}
                </div>

                <div className="rounded-xl border border-hairline bg-background/45 p-3">
                  <SectionLabel>What each poster shows</SectionLabel>
                  <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">Keep the essentials visible; turn on extra metadata only when it helps your workflow.</p>
                  <div className="mt-3 grid gap-2 sm:grid-cols-2">
                    <SwitchRow label="Title" description="The movie or series name" checked={displayOptions.showTitle} onCheckedChange={(showTitle) => setDisplayOptions({ ...displayOptions, showTitle })} />
                    <SwitchRow label="Year & monitoring" description="Release year and monitored state" checked={displayOptions.showMeta} onCheckedChange={(showMeta) => setDisplayOptions({ ...displayOptions, showMeta })} />
                    <SwitchRow label="Availability" description="Missing, downloading, or imported" checked={displayOptions.showStatusPill} onCheckedChange={(showStatusPill) => setDisplayOptions({ ...displayOptions, showStatusPill })} />
                    <SwitchRow label="Quality" description="Current or target quality" checked={displayOptions.showQualityBadge} onCheckedChange={(showQualityBadge) => setDisplayOptions({ ...displayOptions, showQualityBadge })} />
                    <SwitchRow label="Rating" description="The preferred metadata score" checked={displayOptions.showRating} onCheckedChange={(showRating) => setDisplayOptions({ ...displayOptions, showRating })} />
                  </div>
                </div>
              </div>
            </div>
          ) : null}

          {openPanel === "sort" ? (
            <div className="mt-3 overflow-hidden rounded-2xl border border-hairline bg-surface-1">
              <LibraryControlPanelHeader
                icon={ArrowUpDown}
                eyebrow="Order"
                title="Put the right titles first"
                description="Every available order is performed by the paged catalogue query."
                onClose={() => setOpenPanel(null)}
              />
              <div className="grid gap-[var(--grid-gap)] p-[calc(var(--tile-pad)*0.8)] xl:grid-cols-[minmax(0,1fr)_18rem]">
                <div>
                  <SectionLabel>Sort by</SectionLabel>
                  <div className="mt-2 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                    {sortFieldOptions.map((option) => (
                      <SortChoice key={option.value} label={option.label} selected={sortField === option.value} onClick={() => setSortField(option.value)} />
                    ))}
                  </div>
                </div>
                <div className="rounded-xl border border-hairline bg-background/45 p-3">
                  <SectionLabel>Direction</SectionLabel>
                  <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">Choose whether the smallest or largest value leads.</p>
                  <div className="mt-3 grid gap-2">
                    <SortDirectionChoice icon={ArrowDownAZ} label="Ascending" description="A–Z, oldest first, or lowest value." selected={sortDirection === "asc"} onClick={() => setSortDirection("asc")} />
                    <SortDirectionChoice icon={ArrowUpDown} label="Descending" description="Z–A, newest first, or highest value." selected={sortDirection === "desc"} onClick={() => setSortDirection("desc")} />
                  </div>
                </div>
              </div>
            </div>
          ) : null}

          {openPanel === "filter" ? (
            <div className="mt-3 overflow-hidden rounded-2xl border border-hairline bg-surface-1">
              <LibraryControlPanelHeader
                icon={Filter}
                eyebrow="Refine"
                title="Narrow the library without losing your place"
                description={`You are viewing ${quickFilterConfig.find((filter) => filter.key === quickFilter)?.label.toLowerCase() ?? "all"} titles. Quick filters and search run in the catalogue query.`}
                onClose={() => setOpenPanel(null)}
              />
            <div className="space-y-[calc(var(--field-group-pad)*0.8)] p-[calc(var(--tile-pad)*0.8)]">
              <div className="space-y-[calc(var(--field-group-pad)*0.8)]">
                  <div className="space-y-2">
                    <SectionLabel>Saved library views</SectionLabel>
                    <div className="flex gap-2">
                      <Input value={newPresetName} onChange={(event) => setNewPresetName(event.target.value)} placeholder="Name this filter" className="h-[var(--control-height-sm)]" />
                      <Button type="button" size="sm" onClick={saveCurrentPreset} disabled={isSavingPreset}>
                        {isSavingPreset ? "Saving…" : "Save"}
                      </Button>
                    </div>
                  </div>

                  {savedPresets.length > 0 ? (
                    <div className="space-y-2">
                      {savedPresets.map((preset) => (
                        <div key={preset.id} className="flex items-center justify-between gap-3 rounded-xl border border-hairline bg-background/40 px-3 py-3">
                          <button type="button" className="min-w-0 flex-1 text-left" onClick={() => applyPreset(preset)}>
                            <p className="truncate text-sm font-medium text-foreground">{preset.name}</p>
                            <p className="text-xs text-muted-foreground">
                              {preset.libraryId ? `${libraries.find((library) => library.id === preset.libraryId)?.name ?? "Library"} · ` : "All libraries · "}
                              {preset.quickFilter !== "all" ? `${preset.quickFilter} · ` : ""}
                              Saved search, order, and display settings
                            </p>
                          </button>
                          <Button type="button" size="sm" variant="ghost" onClick={() => deletePreset(preset.id)}>
                            Remove
                          </Button>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <div className="rounded-xl border border-dashed border-hairline bg-background/40 px-4 py-4 text-sm text-muted-foreground">
                      Save a search, quick filter, order, and display choice once, then return to it in one click.
                    </div>
                  )}
                </div>
              </div>
            </div>
          ) : null}
        </div>
      </div>
    </div>
  );
}

function ToolbarMenuButton({
  label,
  icon: Icon,
  active,
  meta,
  onClick
}: {
  label: string;
  icon: typeof Filter;
  active: boolean;
  meta: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "group inline-flex min-h-[var(--library-toolbar-height)] items-center gap-2 rounded-xl px-2.5 pr-3 text-left text-[length:var(--library-toolbar-size)] font-medium transition-all",
        active
          ? "bg-primary/10 text-primary ring-1 ring-inset ring-primary/25"
          : "bg-foreground/[0.04] text-foreground ring-1 ring-inset ring-hairline/60 hover:bg-foreground/[0.06] dark:bg-white/[0.05] dark:ring-white/[0.06]"
      )}
    >
      <span className={cn("flex h-6 w-6 shrink-0 items-center justify-center rounded-lg", active ? "bg-primary/16" : "bg-foreground/[0.06] dark:bg-white/[0.07]")}>
        <Icon className="h-3.5 w-3.5" />
      </span>
      <span className="hidden min-w-0 leading-tight sm:block">
        <span className="block font-semibold">{label}</span>
        <span className={cn("block max-w-28 truncate text-[length:var(--type-micro)] font-medium", active ? "text-primary/75" : "text-muted-foreground")}>{meta}</span>
      </span>
      <span className="sm:hidden font-semibold">{label}</span>
      <ChevronDown className={cn("h-3.5 w-3.5 shrink-0 transition-transform", active && "rotate-180")} />
    </button>
  );
}

function LibraryControlPanelHeader({
  icon: Icon,
  eyebrow,
  title,
  description,
  onClose
}: {
  icon: typeof Filter;
  eyebrow: string;
  title: string;
  description: string;
  onClose: () => void;
}) {
  return (
    <div className="flex items-start justify-between gap-3 border-b border-hairline bg-background/35 px-[calc(var(--tile-pad)*0.8)] py-3">
      <div className="flex min-w-0 items-start gap-3">
        <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-xl border border-primary/20 bg-primary/10 text-primary">
          <Icon className="h-4 w-4" />
        </span>
        <div className="min-w-0">
          <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.16em] text-primary">{eyebrow}</p>
          <h3 className="mt-0.5 font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">{title}</h3>
          <p className="mt-1 max-w-4xl text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{description}</p>
        </div>
      </div>
      <button type="button" onClick={onClose} aria-label={`Close ${eyebrow.toLowerCase()} controls`} className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg text-muted-foreground transition hover:bg-muted hover:text-foreground">
        <X className="h-3.5 w-3.5" />
      </button>
    </div>
  );
}

function LibraryViewChoice({
  mode,
  label,
  description,
  selected,
  onClick
}: {
  mode: ViewMode;
  label: string;
  description: string;
  selected: boolean;
  onClick: () => void;
}) {
  const Icon = mode === "grid" ? LayoutGrid : List;
  return (
    <button type="button" onClick={onClick} className={cn("rounded-xl border p-3 text-left transition", selected ? "border-primary/35 bg-primary/[0.09] shadow-[inset_0_0_0_1px_hsl(var(--primary)/0.1)]" : "border-hairline bg-background/45 hover:border-primary/25 hover:bg-background/70")}>
      <div className="flex items-center justify-between gap-3">
        <span className={cn("flex h-8 w-8 items-center justify-center rounded-lg", selected ? "bg-primary/15 text-primary" : "bg-muted text-muted-foreground")}><Icon className="h-4 w-4" /></span>
        {selected ? <span className="rounded-full bg-primary/15 px-2 py-0.5 text-[length:var(--type-micro)] font-bold text-primary">Selected</span> : null}
      </div>
      <p className="mt-3 text-sm font-semibold text-foreground">{label}</p>
      <p className="mt-1 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{description}</p>
    </button>
  );
}

function PosterSizeChoice({ size, selected, onClick }: { size: CardSize; selected: boolean; onClick: () => void }) {
  const labels: Record<CardSize, { label: string; detail: string; height: string }> = {
    sm: { label: "Small", detail: "More titles", height: "h-6" },
    md: { label: "Medium", detail: "Balanced", height: "h-8" },
    lg: { label: "Large", detail: "Artwork first", height: "h-10" }
  };
  const item = labels[size];
  return (
    <button type="button" onClick={onClick} className={cn("rounded-lg border px-2 py-2 text-center transition", selected ? "border-primary/35 bg-primary/[0.09] text-primary" : "border-hairline bg-background/45 text-muted-foreground hover:border-primary/25")}>
      <span className={cn("mx-auto block w-7 rounded-md border", item.height, selected ? "border-primary/45 bg-primary/20" : "border-hairline bg-muted/50")} />
      <span className="mt-1.5 block text-[length:var(--type-caption)] font-semibold">{item.label}</span>
      <span className="block text-[length:var(--type-micro)] opacity-75">{item.detail}</span>
    </button>
  );
}

function SortChoice({ label, selected, onClick }: { label: string; selected: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} className={cn("flex min-h-[var(--control-height)] items-center justify-between rounded-xl border px-3 text-left text-[length:var(--type-caption)] font-semibold transition", selected ? "border-primary/35 bg-primary/[0.09] text-primary" : "border-hairline bg-background/45 text-foreground hover:border-primary/25 hover:bg-background/70")}>
      {label}
      {selected ? <span className="h-1.5 w-1.5 rounded-full bg-primary shadow-[0_0_8px_hsl(var(--primary)/0.75)]" /> : null}
    </button>
  );
}

function SortDirectionChoice({
  icon: Icon,
  label,
  description,
  selected,
  onClick
}: {
  icon: typeof ArrowDownAZ;
  label: string;
  description: string;
  selected: boolean;
  onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick} className={cn("flex items-center gap-3 rounded-xl border p-3 text-left transition", selected ? "border-primary/35 bg-primary/[0.09]" : "border-hairline bg-background/45 hover:border-primary/25 hover:bg-background/70")}>
      <span className={cn("flex h-8 w-8 shrink-0 items-center justify-center rounded-lg", selected ? "bg-primary/15 text-primary" : "bg-muted text-muted-foreground")}><Icon className="h-4 w-4" /></span>
      <span className="min-w-0 flex-1"><span className={cn("block text-[length:var(--type-caption)] font-semibold", selected ? "text-primary" : "text-foreground")}>{label}</span><span className="mt-0.5 block text-[length:var(--type-micro)] leading-snug text-muted-foreground">{description}</span></span>
    </button>
  );
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <p className="text-[length:var(--type-caption)] font-semibold uppercase tracking-[0.18em] text-muted-foreground">
      {children}
    </p>
  );
}


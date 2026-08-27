import { QUICK_FILTER_MARK, type QuickFilter, type SortDirection, type SortField } from "../../lib/library-filters";
import { TITLE_MARK_PRESENTATION, type TitleMark } from "../../lib/status-tones";
import {
  ArrowDownAZ, ArrowUpDown, ChevronDown, Filter, LayoutGrid, LayoutTemplate, List, Search, X
} from "lucide-react";
import React, { useLayoutEffect, useRef, useState } from "react";
import type { CatalogueFacets } from "../../lib/api";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { MenuSelect } from "../ui/menu-select";
import { SwitchRow } from "../ui/switch";
import type { CardSize, DisplayOptions } from "./library-grid";

/**
 * Re-exported, not redeclared. This was its own union — a shorter one than
 * `lib/library-filters.ts`'s — so the two could disagree about what a filter
 * key was, and adding a value to one silently left the other behind.
 */
export type { QuickFilter } from "../../lib/library-filters";
export type ViewMode = "grid" | "list";
/**
 * Re-exported for the same reason `QuickFilter` is. These were redeclared here
 * as four values while `lib/library-filters.ts` declared fourteen — one line
 * below the comment describing that exact defect.
 */
export type { SortDirection, SortField } from "../../lib/library-filters";
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

/**
 * One row: the legend, the counts and the filters at once.
 *
 * These used to be repeated by a summary line above them — Missing, Monitored,
 * Unmonitored and Upgradable appeared twice on the same screen, once as a
 * number you could not click and once as a chip you could. The chips won: they
 * filter, they count, and they are what you scan. Each carries its mark's colour,
 * so the row is also the legend for the shelf below it.
 *
 * "Downloaded" is gone: a movie below its target quality is downloaded too, so
 * the chip selected a set nobody was actually asking for. *Quality met* and
 * *Upgradable* between them say what it was reaching for, and say which of them
 * still needs work. Every label is a mark name, so a chip and the dot on the
 * poster it filters to are the same word.
 *
 * Downloading is deliberately absent until live transfer state is wired in
 * (DESIGN-001 step 5). A chip that can never match anything is worse than no
 * chip at all.
 */
/**
 * One row: the legend, the counts and the filters at once.
 *
 * These used to be repeated by a summary line above them — Missing, Monitored,
 * Unmonitored and Upgradable appeared twice on the same screen, once as a
 * number you could not click and once as a chip you could. The chips won: they
 * filter, they count, and they are what you scan.
 *
 * **Every chip is colour-coded, and the colour is on the number.** It used to be
 * a 6px dot to the left of the label — too small to work as a legend for a
 * shelf of posters, and three of the seven chips had no colour at all. The
 * count is the part you actually read, so the count is what wears the mark.
 *
 * Monitored and Unmonitored get the *monitoring* grammar rather than a hue:
 * a whole dot and a half dot, the same half that appears on a poster. #290 took
 * hue away from things that are not states, and monitoring is a preference, not
 * a rung.
 *
 * "Downloaded" is gone: a movie below its target quality is downloaded too, so
 * the chip selected a set nobody was actually asking for. Downloading is
 * deliberately absent until live transfer state is wired in (DESIGN-001 step 5)
 * — a chip that can never match anything is worse than no chip at all.
 */
export interface QuickFilterChip {
  key: QuickFilter;
  label: string;
  /** The mark this chip selects, when it selects one. */
  mark: TitleMark | null;
  /** Monitoring chips carry the half-dot grammar instead of a hue. */
  monitoring?: "on" | "off";
}

export const quickFilterConfig: QuickFilterChip[] = (
  ["all", "covered", "upgrades", "missing", "upcoming", "monitored", "unmonitored"] as const
).map((key) => {
  // The label and the colour come from the one table, never from here. They
  // were written out by hand — a sixth place colouring a state, three lines
  // under a comment calling this row the legend. A legend that keeps its own
  // copy of the colours is not a legend (#302).
  const mark = QUICK_FILTER_MARK[key];
  if (mark) return { key, label: TITLE_MARK_PRESENTATION[mark].label, mark };
  if (key === "monitored") return { key, label: "Monitored", mark: null, monitoring: "on" as const };
  if (key === "unmonitored") return { key, label: "Unmonitored", mark: null, monitoring: "off" as const };
  return { key, label: "All", mark: null };
});

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

export function ControlRail({ label, facets, actions, controls }: {
  label: string;
  facets: CatalogueFacets | null;
  /** Add, Hunt and Refresh — the two things you can do about what this row shows. */
  actions?: React.ReactNode;
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

  const counts: Partial<Record<QuickFilter, number>> = {
    all: facets?.all ?? 0,
    monitored: facets?.monitored ?? 0,
    unmonitored: facets?.unmonitored ?? 0,
    missing: facets?.missing ?? 0,
    upgrades: facets?.upgrades ?? 0,
    covered: facets?.covered ?? 0,
    upcoming: facets?.upcoming ?? 0
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

            {/*
              A menu Deluno draws, not a native select. The list a native select
              opens is drawn by the operating system — square, flush, highlighted
              in the system blue — so beside the Display and Order menus in this
              same row it read as a different control no matter what colours it
              was given. Same component as the density menu in the header now,
              so there is one styled pick-one rather than two that resemble each
              other.
            */}
            <MenuSelect
              label="Library"
              value={libraryId ?? ""}
              onChange={(value) => setLibraryId(value || null)}
              options={[
                { value: "", label: "All libraries" },
                ...libraries.map((library) => ({ value: library.id, label: library.name }))
              ]}
              className="min-w-[11rem]"
              triggerClassName="min-h-[var(--library-toolbar-height)] bg-foreground/[0.04] px-2.5 text-[length:var(--library-toolbar-size)] font-semibold ring-1 ring-inset ring-hairline/60 hover:bg-foreground/[0.07] dark:bg-white/[0.05] dark:ring-white/[0.06] dark:hover:bg-white/[0.08]"
            />

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
            {/*
              The actions live in this row too, rather than in a band above it.
              That band also carried a count of Missing, Monitored, Unmonitored
              and Upgradable — every one of which is a chip six pixels below,
              with the same number on it. One row now holds the search, the
              scope, the display choices, the filters and the two things you can
              do about them.
            */}
            <div className="ml-auto flex shrink-0 flex-wrap items-center gap-2">{actions}</div>

            <ToolbarMenuButton
              label="Views"
              icon={Filter}
              active={openPanel === "filter"}
              // This panel has only ever contained saved views — the filtering
              // is the chip row below. Calling it "Refine · Quick filters"
              // promised something it did not do.
              meta={savedPresets.length ? `${savedPresets.length} saved` : "Save this view"}
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
                    {/* The mark's own colour, so the chip and the dots on the
                        posters it filters to are one signal rather than two. */}
                    {chip.mark ? (
                      <span aria-hidden className={cn("h-2 w-2 shrink-0 rounded-full", TITLE_MARK_PRESENTATION[chip.mark].dot)} />
                    ) : chip.monitoring ? (
                      <span
                        aria-hidden
                        className={cn(
                          "h-2 w-2 shrink-0 rounded-full",
                          chip.monitoring === "on"
                            ? "bg-foreground/45"
                            : "bg-[linear-gradient(90deg,hsl(var(--foreground)/0.45)_0_50%,hsl(var(--mark-idle))_50%_100%)]"
                        )}
                      />
                    ) : null}
                    <span>{chip.label}</span>
                    {/* The count wears the colour. A 6px dot beside a label is
                        not a legend for a wall of posters; the number is the
                        part you actually read. */}
                    <span
                      className={cn(
                        "tabular rounded-md px-1.5 py-px text-[length:var(--library-badge-size)] font-bold leading-tight",
                        chip.mark
                          ? cn(TITLE_MARK_PRESENTATION[chip.mark].tint, TITLE_MARK_PRESENTATION[chip.mark].text)
                          : active
                            ? "bg-primary/15 text-primary dark:bg-primary/20"
                            : "bg-foreground/[0.06] text-muted-foreground dark:bg-white/[0.07]"
                      )}
                    >
                      {counts[chip.key] ?? 0}
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
              <div className="grid gap-[var(--grid-gap)] p-[calc(var(--tile-pad)*0.8)] xl:grid-cols-[minmax(0,1.05fr)_minmax(320px,0.95fr)] xl:gap-0">
                <div className="space-y-[var(--grid-gap)] xl:pr-[var(--grid-gap)]">
                  <div>
                    <SectionLabel>Layout</SectionLabel>
                    <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">Pick the view that best fits the job in front of you.</p>
                  </div>
                  <div className="grid gap-2 sm:grid-cols-2">
                    <LibraryViewChoice mode="grid" label="Poster grid" description="Artwork-led browsing for your collection." selected={view === "grid"} onClick={() => setView("grid")} />
                    <LibraryViewChoice mode="list" label="Compact list" description="More titles and file details in less space." selected={view === "list"} onClick={() => setView("list")} />
                  </div>

                  {view === "grid" ? (
                    <div>
                      <SectionLabel>Poster size</SectionLabel>
                      <div className="mt-2 grid grid-cols-3 gap-2">
                        {(["sm", "md", "lg"] as CardSize[]).map((size) => (
                          <PosterSizeChoice key={size} size={size} selected={cardSize === size} onClick={() => changeSize(size)} />
                        ))}
                      </div>
                    </div>
                  ) : null}
                </div>

                {/*
                  One column, not two, and no card around it. Five rows across
                  two columns left the fifth stranded beside an empty cell, and
                  the card then held a box open below them that nothing filled.
                  The section label already says what this group is; a border
                  round it says so again. Stacked and divided, the rows read the
                  way every other settings list in Deluno does.
                */}
                {/*
                  A rule between the two halves rather than a box around each:
                  one line says "these are separate" where two borders said it
                  twice. Equal gaps either side of it, and only once the panel is
                  actually side by side — stacked, the section labels already do
                  the separating.
                */}
                <div className="xl:flex xl:flex-col xl:border-l xl:border-hairline xl:pl-[var(--grid-gap)]">
                  <SectionLabel>What each poster shows</SectionLabel>
                  <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">Keep the essentials visible; turn on extra metadata only when it helps your workflow.</p>
                  {/*
                    The rows share out whatever height the choices opposite them
                    take, rather than stopping short and leaving the panel
                    lopsided. Stretching beats a hand-picked row height, which
                    would only be right at one density.
                  */}
                  <div className="mt-2 divide-y divide-hairline xl:flex xl:flex-1 xl:flex-col xl:[&>div]:flex-1">
                    <SwitchRow label="Title" description="The movie or series name" checked={displayOptions.showTitle} onCheckedChange={(showTitle) => setDisplayOptions({ ...displayOptions, showTitle })} />
                    <SwitchRow label="Year & monitoring" description="Release year and monitored state" checked={displayOptions.showMeta} onCheckedChange={(showMeta) => setDisplayOptions({ ...displayOptions, showMeta })} />
                    <SwitchRow label="Status mark" description="Missing, Upgradable, Quality met or Upcoming" checked={displayOptions.showStatusPill} onCheckedChange={(showStatusPill) => setDisplayOptions({ ...displayOptions, showStatusPill })} />
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
                eyebrow="Views"
                title="Come back to this exact view"
                description={`You are viewing ${quickFilterConfig.find((filter) => filter.key === quickFilter)?.label.toLowerCase() ?? "all"} titles. Save the search, filter, order and display together and return to them in one click.`}
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


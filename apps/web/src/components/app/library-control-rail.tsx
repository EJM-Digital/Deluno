import { QUICK_FILTER_MARK, type MonitoringFilter, type QuickFilter, type SortDirection, type SortField } from "../../lib/library-filters";
import { TITLE_MARK_PRESENTATION, type TitleMark } from "../../lib/status-tones";
import { MARK_DOT_SIZE } from "../ui/title-mark";
import {
  ArrowDown, ArrowUp, ArrowUpDown, ChevronDown, Filter, LayoutGrid, LayoutTemplate, List, Search
} from "lucide-react";
import React, { useLayoutEffect, useRef, useState } from "react";
import type { CatalogueFacets } from "../../lib/api";
import type { FilterCondition, LibraryControlSet } from "../../lib/library-controls";
import { describeCondition } from "../../lib/library-controls";
import { LibraryFilterPanel } from "./library-filter-panel";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";
import { Drawer } from "../ui/drawer";
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
export type { MonitoringFilter, SortDirection, SortField } from "../../lib/library-filters";

export interface SavedFilterPreset {
  id: string;
  name: string;
  libraryId: string | null;
  quickFilter: QuickFilter;
  /** The other axis, saved with the view — dropping it would not be the same view. */
  monitoring: MonitoringFilter;
  sortField: SortField;
  sortDirection: SortDirection;
  viewMode: ViewMode;
  cardSize: CardSize;
  displayOptions: DisplayOptions;
  /**
   * The conditions narrowing the shelf.
   *
   * Saved with the view, because a view that restores the shelf you were
   * looking at but not the filters that produced it is not the view you saved —
   * and the difference would be invisible until you counted the titles.
   */
  conditions: FilterCondition[];
}

/**
 * One row: the legend, the counts and the filters at once.
 *
 * These used to be repeated by a summary line above them — Missing, Monitored,
 * Unmonitored and Upgradable appeared twice on the same screen, once as a
 * number you could not click and once as a chip you could. The chips won: they
 * filter, they count, and they are what you scan.
 *
 * **Every chip is a rung, and every rung has a colour.** Monitored and
 * Unmonitored used to sit in here too, and they were the two that could not be
 * given a colour — because monitoring is not a state, it is whether Deluno acts
 * on one, and it multiplies across all four. They are their own control now, so
 * this row is honestly the legend for the shelf below it, and the two questions
 * can be asked *together*: "missing, and I have told Deluno to leave it alone"
 * was unaskable while they shared one value.
 *
 * **The colour is on the number.** It used to be a 6px dot to the left of the
 * label — too small to work as a legend for a wall of posters. The count is the
 * part you read, so the count wears the mark.
 */
export interface QuickFilterChip {
  key: QuickFilter;
  label: string;
  /** The mark this chip selects. Only `all` has none. */
  mark: TitleMark | null;
}

export const quickFilterConfig: QuickFilterChip[] = (
  ["all", "covered", "upgrades", "missing", "upcoming"] as const
).map((key) => {
  // The label and the colour come from the one table, never from here. They
  // were written out by hand — a sixth place colouring a state, three lines
  // under a comment calling this row the legend. A legend that keeps its own
  // copy of the colours is not a legend (#302).
  const mark = QUICK_FILTER_MARK[key];
  return mark
    ? { key, label: TITLE_MARK_PRESENTATION[mark].label, mark }
    : { key, label: "All", mark: null };
});

export function isQuickFilter(value: string | null): value is QuickFilter {
  return quickFilterConfig.some((filter) => filter.key === value);
}

export interface LibraryControls {
  query: string;
  setQuery: (value: string) => void;
  libraryId: string | null;
  setLibraryId: (value: string | null) => void;
  libraries: Array<{ id: string; name: string }>;
  quickFilter: QuickFilter;
  setQuickFilter: (value: QuickFilter) => void;
  monitoring: MonitoringFilter;
  setMonitoring: (value: MonitoringFilter) => void;
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
  /** What the server says this media kind can be filtered by, ordered by and draw. */
  controlSet: LibraryControlSet;
  conditions: FilterCondition[];
  setConditions: (value: FilterCondition[]) => void;
  clearConditions: () => void;
  savedPresets: SavedFilterPreset[];
  newPresetName: string;
  setNewPresetName: (value: string) => void;
  isSavingPreset: boolean;
  saveCurrentPreset: () => void | Promise<void>;
  applyPreset: (preset: SavedFilterPreset) => void;
  deletePreset: (presetId: string) => void;
  activeFilterCount: number;
}

/**
 * Two rows, each with one job — and, below them, one panel at a time, each
 * asking one question.
 *
 * It was one row with nine things on it, and James read it off the page: "all
 * too much on one line and looks so busy". The first fix split the row by what
 * you are doing: **search and act** on top, **narrow and arrange** below. That
 * part held.
 *
 * The second fix merged Display and Order into one **View** panel on the
 * grounds that they were "one question asked twice", and that part did not:
 * James read the result as overcomplicated, because the merge put layout,
 * ordering and eleven per-poster switches behind one button while filtering sat
 * behind another. Ordering is not "how do I want to look at this" — it is which
 * title you find first, and it is the control you reach for most.
 *
 * So the arrangement half is three controls rather than one large panel, and
 * there is one rule for which shape each takes:
 *
 * - **pick one thing → a menu.** Library, and Sort. One click to open, one to
 *   choose, and it costs the page nothing.
 * - **build something → a drawer.** Filter and View, on the right, the same
 *   surface every editor in Deluno already uses.
 *
 * James asked for the drawers: *"what if instead of having it drop down we have
 * all these open as drawers similar to the setup stuff"*, and *"we compress
 * options and this other button into a view button"*. Both are right. The
 * inline panels pushed the shelf down the page every time one opened, which is
 * the same family of problem as a control deciding the layout that decides the
 * control; a drawer is over the top of the shelf and moves nothing. And the
 * layout toggle and the poster options were two controls asking one question —
 * how is this drawn — so they are one **View** now.
 *
 * Sort stayed a menu, which is the one place this deviates. It is a single
 * choice from a short list and it is the control you reach for most; a
 * full-height drawer to change "Title" to "Added" is a lot of travel for one
 * click.
 *
 * Every list behind them is served by `/api/{movies|series}/controls`, so a TV
 * shelf offers TV controls and a film shelf film ones without this file knowing
 * which it is drawing.
 */
export function ControlRail({ label, variant, facets, actions, controls }: {
  label: string;
  variant: "movies" | "shows";
  facets: CatalogueFacets | null;
  /** Add, Search and Refresh — the things you can do about what this row shows. */
  actions?: React.ReactNode;
  controls: LibraryControls;
}) {
  const {
    query, setQuery, libraryId, setLibraryId, libraries, quickFilter, setQuickFilter, monitoring, setMonitoring,
    sortField, setSortField, sortDirection, setSortDirection, view, setView, cardSize, changeSize,
    displayOptions, setDisplayOptions, controlSet, conditions, setConditions, clearConditions,
    savedPresets, newPresetName, setNewPresetName, isSavingPreset, saveCurrentPreset, applyPreset,
    deletePreset, activeFilterCount
  } = controls;

  const [openPanel, setOpenPanel] = useState<"sort" | "filter" | "view" | null>(null);
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
    missing: facets?.missing ?? 0,
    upgrades: facets?.upgrades ?? 0,
    covered: facets?.covered ?? 0,
    upcoming: facets?.upcoming ?? 0
  };

  const sortLabel = controlSet.sortFields.find((option) => option.id === sortField)?.label ?? sortField;
  const fieldsById = new Map(controlSet.filterFields.map((field) => [field.id, field]));
  const toggle = (panel: "sort" | "filter" | "view") =>
    setOpenPanel((current) => (current === panel ? null : panel));

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
              Search and act. Nothing here narrows anything.

              The height is set on the container, not on each button. It was on
              each: the box is 40px, `size="sm"` is 32 and `size="icon"` is 36,
              so one line carried three heights and the row read as ragged. One
              rule here governs whatever is passed in, including the next action
              somebody adds.
            */}
            <div className={cn(
              "ml-auto flex shrink-0 flex-wrap items-center gap-2",
              "[&_button]:h-[var(--library-toolbar-height)]",
              "[&_button[data-slot=icon-action]]:w-[var(--library-toolbar-height)]"
            )}>{actions}</div>
          </div>

          {/*
            Narrow and arrange. The chips are the legend, the counts and the
            filters at once; the controls beside them decide which titles and how
            they are drawn.
          */}
          <div className="mt-2.5 flex flex-wrap items-center gap-x-3 gap-y-2">
            <div ref={pillTrackRef} className="relative flex flex-1 flex-wrap items-center gap-0.5">
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
                    {/* The mark's own colour, at the same diameter as the dot on
                        a small poster, so the chip and the posters it filters to
                        are one signal rather than two. */}
                    {chip.mark ? (
                      <span
                        aria-hidden
                        // The sheen too, so the legend is drawn the same way as
                        // the thing it explains. A legend that leaves out what
                        // makes a mark recognisable is working at half strength.
                        className={cn("shrink-0 rounded-full", TITLE_MARK_PRESENTATION[chip.mark].dot, TITLE_MARK_PRESENTATION[chip.mark].sheen)}
                        style={{ width: MARK_DOT_SIZE, height: MARK_DOT_SIZE }}
                      />
                    ) : null}
                    <span>{chip.label}</span>
                    {/* The count wears the colour: the number is the part you read. */}
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

            {/*
              Two clusters, and the gap between them is the organising idea:
              **which titles** on the left, beside the chips that are also
              narrowing, and **how they are drawn** on the right. They used to be
              one undifferentiated run of five controls in the order they were
              written -- library, layout, order, filter, options -- which is not
              an order at all.
            */}
            <div className="flex shrink-0 flex-wrap items-center gap-x-2 gap-y-2">
              <MenuSelect
                label="Library"
                value={libraryId ?? ""}
                onChange={(value) => setLibraryId(value || null)}
                options={[
                  { value: "", label: "All libraries" },
                  ...libraries.map((library) => ({ value: library.id, label: library.name }))
                ]}
                className="min-w-[10rem]"
                triggerClassName="min-h-[var(--library-toolbar-height)] bg-foreground/[0.04] px-2.5 text-[length:var(--library-toolbar-size)] font-semibold ring-1 ring-inset ring-hairline/60 hover:bg-foreground/[0.07] dark:bg-white/[0.05] dark:ring-white/[0.06] dark:hover:bg-white/[0.08]"
              />

              <ToolbarMenuButton
                label="Filter"
                icon={Filter}
                active={openPanel === "filter"}
                // The number is the point. Monitoring, the library and every
                // condition live behind this button, and a shelf narrowed by
                // something you cannot see is how people lose half their library
                // and conclude Deluno has.
                meta={activeFilterCount > 0 ? `${activeFilterCount} narrowing` : "none"}
                onClick={() => toggle("filter")}
              />

              {/* The rule between narrowing and arranging. */}
              <span aria-hidden className="mx-1 hidden h-5 w-px bg-hairline/70 sm:block dark:bg-white/10" />

              {/*
                Sort is a menu rather than a drawer, and it is the one place this
                row deviates from "build something opens a drawer". It is a
                single choice from a short list and it is the control reached for
                most; a full-height surface to change Title to Added is a lot of
                travel for one click.

                It is `MenuSelect` rather than a popover written here, and that
                is not a style preference. A menu positioned inside this toolbar
                is clipped away by the `overflow-hidden` that keeps the card's
                rounded corners — present in the DOM, invisible on screen — and
                `MenuSelect` already solved that by portalling out of the subtree
                and measuring from its own trigger. Its comment names this very
                toolbar. Writing a second popover here re-created a defect that
                had already been fixed one file away.

                The direction is a button beside it rather than two more rows in
                the list: it is a switch with two states, it is read at a glance,
                and one click is the whole interaction.
              */}
              <div className="flex h-[var(--library-toolbar-height)] items-stretch gap-px overflow-hidden rounded-xl bg-foreground/[0.04] ring-1 ring-inset ring-hairline/60 dark:bg-white/[0.05] dark:ring-white/[0.06]">
                <MenuSelect
                  label="Sort"
                  value={sortField}
                  onChange={setSortField}
                  options={controlSet.sortFields.map((option) => ({
                    value: option.id,
                    label: option.label,
                    hint: option.hint
                  }))}
                  align="end"
                  menuWidth="16rem"
                  leading={<ArrowUpDown className="h-3.5 w-3.5 shrink-0" />}
                  className="min-w-[8.5rem]"
                  triggerClassName="h-full rounded-none bg-transparent px-2.5 text-[length:var(--library-toolbar-size)] font-semibold hover:bg-foreground/[0.05] dark:hover:bg-white/[0.05]"
                />
                <button
                  type="button"
                  onClick={() => setSortDirection(sortDirection === "asc" ? "desc" : "asc")}
                  aria-label={sortDirection === "asc" ? "Ascending — switch to descending" : "Descending — switch to ascending"}
                  title={sortDirection === "asc" ? "A–Z, oldest first, lowest value" : "Z–A, newest first, highest value"}
                  className="flex w-9 shrink-0 items-center justify-center text-muted-foreground transition-colors hover:bg-foreground/[0.05] hover:text-foreground dark:hover:bg-white/[0.05]"
                >
                  {sortDirection === "asc" ? <ArrowUp className="h-3.5 w-3.5" /> : <ArrowDown className="h-3.5 w-3.5" />}
                </button>
              </div>

              {/*
                Layout and the poster options were two controls asking one
                question. One **View** now, and its drawer holds the answer.
              */}
              <ToolbarMenuButton
                label="View"
                icon={LayoutTemplate}
                active={openPanel === "view"}
                meta={view === "grid" ? `grid · ${POSTER_SIZE_LABEL[cardSize].toLowerCase()}` : "list"}
                onClick={() => toggle("view")}
              />
            </div>
          </div>

        </div>
      </div>

      {/*
        Over the shelf, not above it. These were inline panels that pushed the
        grid down the page every time one opened; a drawer moves nothing, and it
        is the surface every other editor in Deluno already uses.
      */}
      <Drawer
        open={openPanel === "filter"}
        onOpenChange={(next) => setOpenPanel(next ? "filter" : null)}
        title="Filter"
        description={`Pick a saved filter, or build one from ${controlSet.filterFields.length} fields — including the file you actually hold, which Radarr states in its own dialog that it cannot ask about.`}
      >
        <div className="space-y-[var(--grid-gap)] py-[var(--grid-gap)]">
          {/*
            A list of named filters first, then the questions the current one
            asks. That order is the point: Radarr's control is a list you pick
            from, and a form of every possible field is what a shared panel
            becomes once there are more than about six of them.
          */}
          <div className="space-y-2">
            <SectionLabel>Saved filters</SectionLabel>
            {savedPresets.length > 0 ? (
              <div className="space-y-1.5">
                {savedPresets.map((preset) => (
                  <div key={preset.id} className="flex items-center justify-between gap-2 rounded-xl border border-hairline bg-background/40 px-3 py-2">
                    <button type="button" className="min-w-0 flex-1 text-left" onClick={() => applyPreset(preset)}>
                      <p className="truncate text-[length:var(--type-caption)] font-semibold text-foreground">{preset.name}</p>
                      <p className="truncate text-[length:var(--type-micro)] text-muted-foreground">
                        {preset.libraryId ? `${libraries.find((library) => library.id === preset.libraryId)?.name ?? "Library"} · ` : "All libraries · "}
                        {preset.conditions.length > 0
                          ? preset.conditions
                              .map((condition) => describeCondition(condition, fieldsById.get(condition.field)))
                              .join(" · ")
                          : "no narrowing"}
                      </p>
                    </button>
                    <Button type="button" size="sm" variant="ghost" onClick={() => deletePreset(preset.id)}>Remove</Button>
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-[length:var(--type-caption)] text-muted-foreground">
                Nothing saved yet. Build a filter below, name it, and it comes back in one click — with its order and
                its layout, because a view that restores half of what you saved is worse than one that restores none.
              </p>
            )}
            <div className="flex gap-2">
              <Input value={newPresetName} onChange={(event) => setNewPresetName(event.target.value)} placeholder="Name this filter" className="h-[var(--control-height-sm)]" />
              <Button type="button" size="sm" onClick={saveCurrentPreset} disabled={isSavingPreset}>
                {isSavingPreset ? "Saving…" : "Save"}
              </Button>
            </div>
          </div>

          <div className="h-px bg-hairline" />

          <LibraryFilterPanel
            variant={variant}
            fields={controlSet.filterFields}
            conditions={conditions}
            onChange={setConditions}
            onClear={clearConditions}
            monitoring={monitoring}
            onMonitoringChange={setMonitoring}
            monitoredCount={facets?.monitored ?? 0}
            unmonitoredCount={facets?.unmonitored ?? 0}
          />
        </div>
      </Drawer>

      <Drawer
        open={openPanel === "view"}
        onOpenChange={(next) => setOpenPanel(next ? "view" : null)}
        title="View"
        description="The layout, its size, and what each poster carries. Remembered separately for movies and TV."
      >
        <div className="space-y-[var(--grid-gap)] py-[var(--grid-gap)]">
          <div>
            <SectionLabel>Layout</SectionLabel>
            <div className="mt-2 grid gap-2 sm:grid-cols-2">
              <LayoutChoice icon={LayoutGrid} label="Poster grid" description="Artwork-led browsing for your collection." selected={view === "grid"} onClick={() => setView("grid")} />
              <LayoutChoice icon={List} label="Compact list" description="More titles and file details in less space." selected={view === "list"} onClick={() => setView("list")} />
            </div>
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

          {/*
            The switches come from the served declaration, so an option cannot
            exist without a label and a description beside it, and a media kind
            that has one the other does not — a show's next airing — needs
            nothing changed here.
          */}
          <div>
            <SectionLabel>What each poster shows</SectionLabel>
            <div className="mt-2 divide-y divide-hairline">
              {controlSet.posterOptions.map((option) => (
                <SwitchRow
                  key={option.id}
                  label={option.label}
                  description={option.description}
                  checked={displayOptions[option.id] ?? option.defaultOn}
                  onCheckedChange={(checked) => setDisplayOptions({ ...displayOptions, [option.id]: checked })}
                />
              ))}
            </div>
          </div>
        </div>
      </Drawer>
    </div>
  );
}

const POSTER_SIZE_LABEL: Record<CardSize, string> = { sm: "Small", md: "Medium", lg: "Large" };

function ToolbarMenuButton({
  label, icon: Icon, active, meta, onClick
}: { label: string; icon: typeof Filter; active: boolean; meta: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-expanded={active}
      className={cn(
        "inline-flex h-[var(--library-toolbar-height)] items-center gap-1.5 rounded-xl px-2.5 text-left",
        "text-[length:var(--library-toolbar-size)] font-semibold transition-colors",
        active
          ? "bg-primary/10 text-primary ring-1 ring-inset ring-primary/25"
          : "bg-foreground/[0.04] text-foreground ring-1 ring-inset ring-hairline/60 hover:bg-foreground/[0.07] dark:bg-white/[0.05] dark:ring-white/[0.06] dark:hover:bg-white/[0.08]"
      )}
    >
      <Icon className="h-3.5 w-3.5 shrink-0" />
      <span>{label}</span>
      {/* The value, on the same line and quieter than the name. Hidden on a
          narrow screen rather than wrapped, because a wrapped value is what made
          this two lines tall in the first place. */}
      <span className={cn(
        "hidden max-w-32 truncate font-medium md:block",
        active ? "text-primary/70" : "text-muted-foreground"
      )}>
        {meta}
      </span>
      <ChevronDown className={cn("h-3.5 w-3.5 shrink-0 transition-transform", active && "rotate-180")} />
    </button>
  );
}

function PosterSizeChoice({ size, selected, onClick }: { size: CardSize; selected: boolean; onClick: () => void }) {
  const labels: Record<CardSize, { detail: string; height: string }> = {
    sm: { detail: "More titles", height: "h-6" },
    md: { detail: "Balanced", height: "h-8" },
    lg: { detail: "Artwork first", height: "h-10" }
  };
  const item = labels[size];
  return (
    <button type="button" onClick={onClick} className={cn("rounded-lg border px-2 py-2 text-center transition", selected ? "border-primary/35 bg-primary/[0.09] text-primary" : "border-hairline bg-background/45 text-muted-foreground hover:border-primary/25")}>
      <span className={cn("mx-auto block w-7 rounded-md border", item.height, selected ? "border-primary/45 bg-primary/20" : "border-hairline bg-muted/50")} />
      <span className="mt-1.5 block text-[length:var(--type-caption)] font-semibold">{POSTER_SIZE_LABEL[size]}</span>
      <span className="block text-[length:var(--type-micro)] opacity-75">{item.detail}</span>
    </button>
  );
}

function LayoutChoice({
  icon: Icon, label, description, selected, onClick
}: { icon: typeof LayoutGrid; label: string; description: string; selected: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} className={cn("rounded-xl border p-3 text-left transition", selected ? "border-primary/35 bg-primary/[0.09]" : "border-hairline bg-background/45 hover:border-primary/25 hover:bg-background/70")}>
      <div className="flex items-center justify-between gap-3">
        <span className={cn("flex h-8 w-8 items-center justify-center rounded-lg", selected ? "bg-primary/15 text-primary" : "bg-muted text-muted-foreground")}><Icon className="h-4 w-4" /></span>
        {selected ? <span className="rounded-full bg-primary/15 px-2 py-0.5 text-[length:var(--type-micro)] font-bold text-primary">Selected</span> : null}
      </div>
      <p className="mt-3 text-sm font-semibold text-foreground">{label}</p>
      <p className="mt-1 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{description}</p>
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

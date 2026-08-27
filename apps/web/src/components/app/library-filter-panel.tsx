import { useEffect, useState } from "react";
import { Filter, X } from "lucide-react";
import { fetchJson, type QualityModelSnapshot } from "../../lib/api";
import { customFilterCount, isMonitoringFilter, type CustomFilters, type MonitoringFilter } from "../../lib/library-filters";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { MenuSelect } from "../ui/menu-select";

/**
 * Everything that narrows a shelf, in one place.
 *
 * It used to be three: a Monitoring select and a Views button in the toolbar
 * and, for anything else, nothing at all. Bazarr-style "custom filters" were
 * the one thing Radarr does that Deluno had no answer to.
 *
 * The controls are named, typed and finite, deliberately. A field/operator/value
 * rule builder is more expressive and it is what this codebase already tried and
 * deleted (#302): it could express filters nothing could answer, and two of its
 * conditions matched zero rows forever without anybody noticing. Here, every
 * control is one real stored column with one meaning, and asking for something
 * unanswerable is not possible.
 *
 * Options are fetched when the panel first opens, never before: the quality
 * tiers from the same `/api/quality-model` that Library Profiles and Size Rules
 * read, and the genres from the catalogue itself so the list is the whole
 * library rather than whatever happens to be on the current page.
 */
export function LibraryFilterPanel({
  variant,
  filters,
  onChange,
  onClear,
  monitoring,
  onMonitoringChange,
  monitoredCount,
  unmonitoredCount
}: {
  variant: "movies" | "shows";
  filters: CustomFilters;
  onChange: (next: CustomFilters) => void;
  onClear: () => void;
  monitoring: MonitoringFilter;
  onMonitoringChange: (next: MonitoringFilter) => void;
  monitoredCount: number;
  unmonitoredCount: number;
}) {
  const [qualityTiers, setQualityTiers] = useState<string[]>([]);
  const [genres, setGenres] = useState<string[]>([]);

  useEffect(() => {
    let cancelled = false;
    const genresUrl = variant === "movies" ? "/api/movies/genres" : "/api/series/genres";

    void Promise.all([
      fetchJson<QualityModelSnapshot>("/api/quality-model").catch(() => null),
      fetchJson<string[]>(genresUrl).catch(() => [])
    ]).then(([model, catalogueGenres]) => {
      if (cancelled) return;
      // Highest first: a person filtering by quality is nearly always reaching
      // for the top of the ladder.
      setQualityTiers([...(model?.tiers ?? [])].sort((a, b) => b.rank - a.rank).map((tier) => tier.name));
      setGenres(catalogueGenres);
    });

    return () => { cancelled = true; };
  }, [variant]);

  const count = customFilterCount(filters);
  const toggle = (list: string[], value: string) =>
    list.includes(value) ? list.filter((item) => item !== value) : [...list, value];

  return (
    <div className="grid gap-[var(--grid-gap)] p-[calc(var(--tile-pad)*0.8)] xl:grid-cols-2 xl:gap-x-[var(--tile-pad)]">
      <div className="space-y-[var(--grid-gap)]">
        <Group
          label="Monitoring"
          hint="Whether Deluno acts on the title. A separate question from what state it is in, so the two narrow together."
        >
          <MenuSelect
            label="Monitoring"
            value={monitoring}
            onChange={(value) => onMonitoringChange(isMonitoringFilter(value) ? value : "any")}
            options={[
              { value: "any", label: "Any monitoring" },
              { value: "monitored", label: `Monitored (${monitoredCount})` },
              { value: "unmonitored", label: `Not monitored (${unmonitoredCount})` }
            ]}
            className="w-full"
            triggerClassName="min-h-[var(--control-height-sm)] w-full bg-background/50 px-2.5 text-[length:var(--library-toolbar-size)] font-semibold ring-1 ring-inset ring-hairline/60"
          />
        </Group>

        <Group
          label="Quality"
          hint="The tier the file actually is. A title with no file matches none of these — asking for “4K” and being handed things you do not have is not an answer."
        >
          {qualityTiers.length === 0 ? (
            <p className="text-[length:var(--type-caption)] text-muted-foreground">Loading the quality ladder…</p>
          ) : (
            <ChipCloud
              values={qualityTiers}
              selected={filters.qualities}
              onToggle={(value) => onChange({ ...filters, qualities: toggle(filters.qualities, value) })}
            />
          )}
        </Group>

        <Group label="Genre" hint="Every genre you pick has to be present, which is what picking two means.">
          {genres.length === 0 ? (
            <p className="text-[length:var(--type-caption)] text-muted-foreground">
              Nothing in this library has a genre yet. They arrive with metadata.
            </p>
          ) : (
            <ChipCloud
              values={genres}
              selected={filters.genres}
              onToggle={(value) => onChange({ ...filters, genres: toggle(filters.genres, value) })}
            />
          )}
        </Group>
      </div>

      <div className="space-y-[var(--grid-gap)]">
        <Group label="Size on disk" hint="Gigabytes. Leave either end blank for “no limit”.">
          <RangeRow
            unit="GB"
            min={filters.minSizeGb}
            max={filters.maxSizeGb}
            onMin={(minSizeGb) => onChange({ ...filters, minSizeGb })}
            onMax={(maxSizeGb) => onChange({ ...filters, maxSizeGb })}
          />
        </Group>

        <Group label="Year" hint={variant === "movies" ? "Release year." : "The year the show started."}>
          <RangeRow
            unit=""
            min={filters.minYear}
            max={filters.maxYear}
            onMin={(minYear) => onChange({ ...filters, minYear })}
            onMax={(maxYear) => onChange({ ...filters, maxYear })}
          />
        </Group>

        <Group label="Runtime" hint="Minutes.">
          <RangeRow
            unit="min"
            min={filters.minRuntime}
            max={filters.maxRuntime}
            onMin={(minRuntime) => onChange({ ...filters, minRuntime })}
            onMax={(maxRuntime) => onChange({ ...filters, maxRuntime })}
          />
        </Group>

        <Group label="Rated at least" hint="The metadata score, out of ten.">
          <Input
            type="number"
            inputMode="decimal"
            step="0.1"
            min="0"
            max="10"
            value={filters.minRating ?? ""}
            placeholder="Any"
            onChange={(event) => onChange({ ...filters, minRating: parseNumber(event.target.value) })}
            className="h-[var(--control-height-sm)] w-28"
          />
        </Group>

        {count > 0 ? (
          <Button type="button" variant="outline" size="sm" onClick={onClear} className="gap-1.5">
            <X className="h-3.5 w-3.5" />
            Clear {count} filter{count === 1 ? "" : "s"}
          </Button>
        ) : (
          <p className="flex items-center gap-1.5 text-[length:var(--type-caption)] text-muted-foreground">
            <Filter className="h-3.5 w-3.5" />
            Nothing is being narrowed. The shelf is showing everything the row above selects.
          </p>
        )}
      </div>
    </div>
  );
}

function Group({ label, hint, children }: { label: string; hint: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1.5">
      <p className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.1em] text-muted-foreground">{label}</p>
      <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground/80">{hint}</p>
      <div className="pt-1">{children}</div>
    </div>
  );
}

/**
 * A wall of options you pick several of. Chips rather than a multi-select
 * because the answer has to be readable at a glance once it is chosen — a
 * closed select saying "3 selected" is a filter you have to open to understand.
 */
function ChipCloud({ values, selected, onToggle }: { values: string[]; selected: string[]; onToggle: (value: string) => void }) {
  return (
    <div className="flex max-h-40 flex-wrap gap-1.5 overflow-y-auto">
      {values.map((value) => {
        const active = selected.includes(value);
        return (
          <button
            key={value}
            type="button"
            onClick={() => onToggle(value)}
            aria-pressed={active}
            className={cn(
              "rounded-lg px-2.5 py-1 text-[length:var(--library-toolbar-size)] font-medium transition-colors",
              active
                ? "bg-primary/15 text-primary ring-1 ring-inset ring-primary/30"
                : "bg-foreground/[0.05] text-muted-foreground ring-1 ring-inset ring-hairline/60 hover:text-foreground dark:bg-white/[0.05]"
            )}
          >
            {value}
          </button>
        );
      })}
    </div>
  );
}

function RangeRow({
  unit, min, max, onMin, onMax
}: {
  unit: string;
  min: number | null;
  max: number | null;
  onMin: (value: number | null) => void;
  onMax: (value: number | null) => void;
}) {
  return (
    <div className="flex items-center gap-2">
      <Input
        type="number"
        inputMode="decimal"
        value={min ?? ""}
        placeholder="Any"
        onChange={(event) => onMin(parseNumber(event.target.value))}
        className="h-[var(--control-height-sm)] w-24"
        aria-label={`Minimum ${unit || "value"}`}
      />
      <span className="text-[length:var(--type-caption)] text-muted-foreground">to</span>
      <Input
        type="number"
        inputMode="decimal"
        value={max ?? ""}
        placeholder="Any"
        onChange={(event) => onMax(parseNumber(event.target.value))}
        className="h-[var(--control-height-sm)] w-24"
        aria-label={`Maximum ${unit || "value"}`}
      />
      {unit ? <span className="text-[length:var(--type-caption)] text-muted-foreground">{unit}</span> : null}
    </div>
  );
}

/** Blank means "no limit", not zero — which would be a filter that matches nothing. */
function parseNumber(raw: string): number | null {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : null;
}

import { X } from "lucide-react";
import { Button } from "../ui/button";
import { Field } from "../ui/field";
import { Input } from "../ui/input";
import { Select } from "../ui/select";
import {
  LANGUAGES,
  NO_FILTERS,
  RATING_STEPS,
  genresFor,
  type DiscoverFilters,
  type DiscoverKind
} from "../../lib/discover-filters";
import { cn } from "../../lib/utils";

/**
 * Discover's filters (#1429): released or upcoming, a year range, genre, the lowest rating, the original language and the order,
 * for films and for shows. Each one is TMDb's own discover field; the server passes them to the metadata broker.
 */
export function DiscoverFilterBar({
  kind,
  filters,
  onChange
}: {
  kind: DiscoverKind;
  filters: DiscoverFilters;
  onChange: (filters: DiscoverFilters) => void;
}) {
  const noun = kind === "series" ? "shows" : "films";
  const set = (patch: Partial<DiscoverFilters>) => onChange({ ...filters, ...patch });
  const toggleGenre = (id: number) =>
    set({ genres: filters.genres.includes(id) ? filters.genres.filter((existing) => existing !== id) : [...filters.genres, id] });

  return (
    <div className="grid gap-(--grid-gap) border-b border-hairline p-(--tile-pad)" role="group" aria-label="Discover filters">
      <div className="grid gap-(--grid-gap) sm:grid-cols-2 lg:grid-cols-6">
        <Field label="Release">
          <Select
            aria-label="Release"
            value={filters.release}
            onChange={(event) => set({ release: event.target.value as DiscoverFilters["release"] })}
            options={[
              { value: "", label: "Released or upcoming" },
              { value: "released", label: "Already released" },
              { value: "upcoming", label: "Not out yet" }
            ]}
          />
        </Field>
        <Field label="From year">
          <Input aria-label="From year" inputMode="numeric" placeholder="1990" value={filters.yearFrom} onChange={(event) => set({ yearFrom: event.target.value.replace(/\D/g, "").slice(0, 4) })} />
        </Field>
        <Field label="To year">
          <Input aria-label="To year" inputMode="numeric" placeholder="2030" value={filters.yearTo} onChange={(event) => set({ yearTo: event.target.value.replace(/\D/g, "").slice(0, 4) })} />
        </Field>
        <Field label="Lowest rating">
          <Select
            aria-label="Lowest rating"
            value={filters.ratingMin}
            onChange={(event) => set({ ratingMin: event.target.value })}
            options={[{ value: "", label: "Any rating" }, ...RATING_STEPS.map((step) => ({ value: step, label: `${step} and up` }))]}
          />
        </Field>
        <Field label="Original language">
          <Select
            aria-label="Original language"
            value={filters.language}
            onChange={(event) => set({ language: event.target.value })}
            options={[{ value: "", label: "Any language" }, ...LANGUAGES]}
          />
        </Field>
        <Field label="Order">
          <Select
            aria-label="Order"
            value={filters.sort}
            onChange={(event) => set({ sort: event.target.value as DiscoverFilters["sort"] })}
            options={[
              { value: "", label: "Most popular first" },
              { value: "rating", label: "Highest rated first" },
              { value: "newest", label: "Newest first" },
              { value: "oldest", label: "Oldest first" }
            ]}
          />
        </Field>
      </div>
      <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label={`Genres of ${noun}`}>
        {genresFor(kind).map((genre) => {
          const on = filters.genres.includes(genre.id);
          return (
            <button
              key={genre.id}
              type="button"
              aria-pressed={on}
              onClick={() => toggleGenre(genre.id)}
              className={cn(
                "h-7 rounded-full border px-3 text-(length:--type-caption) font-medium transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring",
                on ? "border-primary/40 bg-primary/10 text-primary" : "border-hairline text-muted-foreground hover:bg-secondary hover:text-foreground"
              )}
            >
              {genre.name}
            </button>
          );
        })}
        <Button type="button" size="sm" variant="ghost" className="ml-auto" onClick={() => onChange(NO_FILTERS)}>
          <X className="h-3.5 w-3.5" /> Clear filters
        </Button>
      </div>
    </div>
  );
}

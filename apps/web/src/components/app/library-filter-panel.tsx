import { useEffect, useMemo, useState } from "react";
import { Check, Filter, Plus, Search, X } from "lucide-react";
import { fetchJson, type QualityModelSnapshot } from "../../lib/api";
import {
  FILTER_GROUPS,
  OPERATOR_LABELS,
  describeCondition,
  isRelativeOperator,
  initialValues,
  isCompleteCondition,
  isMultiValue,
  operatorTakesValues,
  type FilterCondition,
  type FilterFieldSpec,
  type FilterOperator,
  type MediaVariant
} from "../../lib/library-controls";
import { isMonitoringFilter, type MonitoringFilter } from "../../lib/library-filters";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { MenuSelect } from "../ui/menu-select";

/**
 * Everything that narrows a shelf: your saved filters first, then the questions
 * this one is asking, then a field to add.
 *
 * **A list of filters, not a form of fields.** That is Radarr's shape and it is
 * the right one — his own instance has twenty-four he built — but the fields
 * behind it are a closed, typed, server-declared set rather than free text. So
 * a panel holding two conditions is two rows, and one holding fifteen is fifteen;
 * it does not begin as a wall of controls, most of them inert on whichever shelf
 * you are looking at, which is exactly what a shared quality/size/genre/year/
 * runtime/rating form became once the count went past six (#324).
 *
 * The vocabulary is fetched, never declared here. `library-controls.ts` asks
 * `/api/{movies|series}/controls` and the panel renders what it is given, so
 * a TV shelf offers TV fields and a film shelf film ones without a single
 * `variant ===` in this file. The genre list and the quality ladder keep their
 * own endpoints because those are data rather than declarations — the ladder is
 * the same `/api/quality-model` Library Profiles and Size Rules read, so a filter
 * can never offer a tier the ladder does not have.
 */
export function LibraryFilterPanel({
  variant,
  fields,
  conditions,
  onChange,
  onClear,
  monitoring,
  onMonitoringChange,
  monitoredCount,
  unmonitoredCount
}: {
  variant: MediaVariant;
  fields: FilterFieldSpec[];
  conditions: FilterCondition[];
  onChange: (next: FilterCondition[]) => void;
  onClear: () => void;
  monitoring: MonitoringFilter;
  onMonitoringChange: (next: MonitoringFilter) => void;
  monitoredCount: number;
  unmonitoredCount: number;
}) {
  const [qualityTiers, setQualityTiers] = useState<string[]>([]);
  const [genres, setGenres] = useState<string[]>([]);
  const [fieldSearch, setFieldSearch] = useState("");
  const [picking, setPicking] = useState(false);

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

  const byId = useMemo(() => new Map(fields.map((field) => [field.id, field])), [fields]);

  const matches = useMemo(() => {
    const needle = fieldSearch.trim().toLowerCase();
    return fields.filter((field) =>
      !needle || field.label.toLowerCase().includes(needle) || field.hint.toLowerCase().includes(needle));
  }, [fields, fieldSearch]);

  function add(field: FilterFieldSpec) {
    const operator = field.operators[0]!;
    onChange([...conditions, { field: field.id, operator, values: initialValues(field, operator) }]);
    setPicking(false);
    setFieldSearch("");
  }

  function update(index: number, next: FilterCondition) {
    onChange(conditions.map((condition, position) => (position === index ? next : condition)));
  }

  function remove(index: number) {
    onChange(conditions.filter((_, position) => position !== index));
  }

  return (
    <div className="space-y-[var(--grid-gap)] p-[calc(var(--tile-pad)*0.8)]">
      <Group
        label="Monitoring"
        hint="Whether Deluno acts on the title. Its own axis, so it narrows together with everything below rather than instead of it."
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
          className="w-full sm:max-w-xs"
          triggerClassName="min-h-[var(--control-height-sm)] w-full bg-background/50 px-2.5 text-[length:var(--library-toolbar-size)] font-semibold ring-1 ring-inset ring-hairline/60"
        />
      </Group>

      <div className="space-y-2">
        <SectionLabel>What this filter asks</SectionLabel>
        {conditions.length === 0 ? (
          <p className="flex items-center gap-1.5 text-[length:var(--type-caption)] text-muted-foreground">
            <Filter className="h-3.5 w-3.5" />
            Nothing is being narrowed. The shelf is showing everything the row above selects.
          </p>
        ) : (
          <div className="space-y-1.5">
            {conditions.map((condition, index) => (
              <ConditionRow
                key={`${condition.field}-${index}`}
                condition={condition}
                field={byId.get(condition.field)}
                qualityTiers={qualityTiers}
                genres={genres}
                onChange={(next) => update(index, next)}
                onRemove={() => remove(index)}
              />
            ))}
          </div>
        )}
      </div>

      {picking ? (
        <div className="space-y-2 rounded-xl border border-hairline bg-background/40 p-3">
          <div className="flex items-center gap-2">
            <Search className="h-3.5 w-3.5 shrink-0 text-muted-foreground/60" />
            <Input
              autoFocus
              value={fieldSearch}
              onChange={(event) => setFieldSearch(event.target.value)}
              placeholder="Search fields — codec, certification, last searched…"
              className="h-[var(--control-height-sm)] border-0 bg-transparent px-0 shadow-none focus-visible:ring-0"
            />
            <button
              type="button"
              onClick={() => { setPicking(false); setFieldSearch(""); }}
              aria-label="Stop adding a field"
              className="flex h-6 w-6 shrink-0 items-center justify-center rounded-lg text-muted-foreground hover:bg-muted hover:text-foreground"
            >
              <X className="h-3.5 w-3.5" />
            </button>
          </div>

          <div className="max-h-72 space-y-3 overflow-y-auto pr-1">
            {FILTER_GROUPS.map((group) => {
              const inGroup = matches.filter((field) => field.group === group.key);
              if (inGroup.length === 0) return null;
              return (
                <div key={group.key}>
                  <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.14em] text-muted-foreground">
                    {group.label}
                  </p>
                  <p className="text-[length:var(--type-micro)] text-muted-foreground/70">{group.blurb}</p>
                  <div className="mt-1.5 grid gap-1 sm:grid-cols-2">
                    {inGroup.map((field) => (
                      <button
                        key={field.id}
                        type="button"
                        onClick={() => add(field)}
                        className="rounded-lg px-2.5 py-1.5 text-left transition hover:bg-foreground/[0.06] dark:hover:bg-white/[0.06]"
                      >
                        <span className="block text-[length:var(--type-caption)] font-semibold text-foreground">{field.label}</span>
                        <span className="block truncate text-[length:var(--type-micro)] text-muted-foreground">{field.hint}</span>
                      </button>
                    ))}
                  </div>
                </div>
              );
            })}
            {matches.length === 0 ? (
              <p className="text-[length:var(--type-caption)] text-muted-foreground">
                Nothing here is called that. Every field is a real column Deluno stores, so if it is not listed it is not
                something this library records yet.
              </p>
            ) : null}
          </div>
        </div>
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <Button type="button" variant="outline" size="sm" onClick={() => setPicking(true)} className="gap-1.5">
            <Plus className="h-3.5 w-3.5" />
            Add a field
          </Button>
          <span className="text-[length:var(--type-micro)] text-muted-foreground">
            {fields.length} to choose from.
          </span>
          {conditions.length > 0 ? (
            <Button type="button" variant="ghost" size="sm" onClick={onClear} className="ml-auto gap-1.5">
              <X className="h-3.5 w-3.5" />
              Clear {conditions.length} filter{conditions.length === 1 ? "" : "s"}
            </Button>
          ) : null}
        </div>
      )}
    </div>
  );
}

/**
 * One question: the field, how it is compared, and what to. The field itself is
 * a label rather than a select — you swap a field by removing the row and adding
 * another, which keeps the row readable as a sentence.
 */
function ConditionRow({
  condition,
  field,
  qualityTiers,
  genres,
  onChange,
  onRemove
}: {
  condition: FilterCondition;
  field: FilterFieldSpec | undefined;
  qualityTiers: string[];
  genres: string[];
  onChange: (next: FilterCondition) => void;
  onRemove: () => void;
}) {
  if (!field) {
    // Served field lists change with a deploy; a saved filter naming one that is
    // gone says so rather than narrowing by something invisible.
    return (
      <div className="flex items-center justify-between gap-2 rounded-xl border border-warning/40 bg-warning/[0.07] px-3 py-2">
        <p className="text-[length:var(--type-caption)] text-foreground">
          This filter asks for “{condition.field}”, which this library no longer records.
        </p>
        <RemoveButton onClick={onRemove} />
      </div>
    );
  }

  const options = field.valueKind === "quality" ? qualityTiers
    : field.valueKind === "genre" ? genres
    : field.options ?? [];

  return (
    <div className="rounded-xl border border-hairline bg-background/40 px-3 py-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-[length:var(--type-caption)] font-semibold text-foreground">{field.label}</span>

        <MenuSelect
          label={`How ${field.label} is compared`}
          value={condition.operator}
          onChange={(value) => onChange({ ...condition, operator: value as FilterOperator, values: [] })}
          options={field.operators.map((operator) => ({ value: operator, label: OPERATOR_LABELS[operator] }))}
          className="min-w-[9rem]"
          triggerClassName="min-h-[var(--control-height-sm)] bg-background/60 px-2.5 text-[length:var(--library-toolbar-size)] ring-1 ring-inset ring-hairline/60"
        />

        <ValueControl field={field} condition={condition} options={options} onChange={onChange} />

        <RemoveButton onClick={onRemove} className="ml-auto" />
      </div>
      {/*
        A row waiting for a value says so rather than looking finished. It is not
        sent while it is incomplete, so the shelf behind it is unchanged — and
        that has to be visible, or the panel looks like it is filtering and the
        shelf looks like it is not.
      */}
      <p className={cn(
        "mt-1 text-[length:var(--type-micro)]",
        isCompleteCondition(condition) ? "text-muted-foreground" : "text-warning"
      )}>
        {isCompleteCondition(condition)
          ? describeCondition(condition, field)
          : `Nothing is narrowed by this yet — ${field.label.toLowerCase()} needs a value.`}
      </p>
    </div>
  );
}

function ValueControl({
  field,
  condition,
  options,
  onChange
}: {
  field: FilterFieldSpec;
  condition: FilterCondition;
  options: string[];
  onChange: (next: FilterCondition) => void;
}) {
  if (!operatorTakesValues(condition.operator)) return null;

  const single = (value: string) => onChange({ ...condition, values: value ? [value] : [] });

  if (isMultiValue(field, condition.operator)) {
    if (options.length === 0) {
      return (
        <span className="text-[length:var(--type-caption)] text-muted-foreground">
          Nothing in this library has one yet. They arrive with metadata.
        </span>
      );
    }
    const toggle = (value: string) =>
      onChange({
        ...condition,
        values: condition.values.includes(value)
          ? condition.values.filter((item) => item !== value)
          : [...condition.values, value]
      });
    return (
      <div className="flex max-h-32 flex-1 flex-wrap gap-1.5 overflow-y-auto">
        {options.map((value) => {
          const active = condition.values.includes(value);
          return (
            <button
              key={value}
              type="button"
              onClick={() => toggle(value)}
              aria-pressed={active}
              className={cn(
                "rounded-lg px-2.5 py-1 text-[length:var(--library-toolbar-size)] font-medium transition-colors",
                active
                  ? "bg-primary/15 text-primary ring-1 ring-inset ring-primary/30"
                  : "bg-foreground/[0.05] text-muted-foreground ring-1 ring-inset ring-hairline/60 hover:text-foreground dark:bg-white/[0.05]"
              )}
            >
              {value}
              {active ? <Check className="ml-1 inline h-3 w-3" /> : null}
            </button>
          );
        })}
      </div>
    );
  }

  if (field.valueKind === "boolean") {
    return (
      <MenuSelect
        label={field.label}
        value={condition.values[0] ?? "true"}
        onChange={single}
        options={[{ value: "true", label: "Yes" }, { value: "false", label: "No" }]}
        className="min-w-[6rem]"
        triggerClassName="min-h-[var(--control-height-sm)] bg-background/60 px-2.5 text-[length:var(--library-toolbar-size)] ring-1 ring-inset ring-hairline/60"
      />
    );
  }

  // "in the last N days", "in the next N days" and "not in the last N days"
  // take a count, not a date — which is the whole point of them. Radarr's date
  // filters are absolute, so "added recently" there is a filter you rewrite
  // every month.
  const relative = isRelativeOperator(condition.operator);
  const type = relative ? "number"
    : field.valueKind === "date" ? "date"
    : field.valueKind === "text" ? "text"
    : "number";

  const unit = relative ? "days"
    : field.valueKind === "gigabytes" ? "GB"
    : field.valueKind === "minutes" ? "min"
    : "";

  return (
    <div className="flex items-center gap-1.5">
      <Input
        type={type}
        inputMode={type === "number" ? "decimal" : undefined}
        step={field.valueKind === "rating" || field.valueKind === "decimal" ? "0.1" : undefined}
        value={condition.values[0] ?? ""}
        placeholder={relative ? "30" : field.valueKind === "text" ? "Type a value" : "Any"}
        onChange={(event) => single(event.target.value)}
        aria-label={field.label}
        className={cn("h-[var(--control-height-sm)]", type === "text" ? "w-44" : "w-32")}
      />
      {unit ? <span className="text-[length:var(--type-caption)] text-muted-foreground">{unit}</span> : null}
    </div>
  );
}

function RemoveButton({ onClick, className }: { onClick: () => void; className?: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label="Remove this filter"
      className={cn(
        "flex h-6 w-6 shrink-0 items-center justify-center rounded-lg text-muted-foreground transition hover:bg-muted hover:text-foreground",
        className
      )}
    >
      <X className="h-3.5 w-3.5" />
    </button>
  );
}

function Group({ label, hint, children }: { label: string; hint: string; children: React.ReactNode }) {
  return (
    <div className="space-y-1.5">
      <SectionLabel>{label}</SectionLabel>
      <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground/80">{hint}</p>
      <div className="pt-1">{children}</div>
    </div>
  );
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <p className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.1em] text-muted-foreground">
      {children}
    </p>
  );
}

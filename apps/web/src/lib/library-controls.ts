/**
 * What the toolbar above a shelf may offer, fetched from the server that has to
 * perform it.
 *
 * The browser used to declare all three of these itself: `sortFieldOptions` beside
 * the server's `CatalogueSortFields`, `DisplayOptions` beside nothing at all, and
 * nine filter fields hard-coded into a panel. `variant` reached that panel and
 * decided exactly two things — a hint under Year, and which `/genres` endpoint to
 * call — so a TV shelf was offered a film's controls and a film shelf a show's.
 *
 * One list now, served by `GET /api/{movies|series}/controls`, per media kind.
 * The interface cannot offer a filter the query cannot answer, because there is
 * nowhere left to write a second list. That is the same rule
 * `MediaTableMap.For(MediaKind)` follows on the server (ADR-001): shared from the
 * first line, and anything genuinely different declared once.
 */
import { fetchJson } from "./api";

export type MediaVariant = "movies" | "shows";

/** Which half of a title a field asks about, and the heading it sits under. */
export type FilterFieldGroup = "title" | "file" | "time" | "decision";

export type FilterValueKind =
  | "text" | "integer" | "decimal" | "year" | "minutes" | "gigabytes"
  | "rating" | "date" | "boolean" | "quality" | "genre" | "enum";

/**
 * The operator tokens, spelled exactly as they travel on the query string.
 *
 * Not a second naming of the server's enum — the server projects to these very
 * tokens when it serves the field list, so there is one word per comparison
 * across the whole product.
 */
export type FilterOperator =
  | "in" | "notin" | "all" | "is" | "isnot" | "min" | "max"
  | "has" | "nothas" | "starts" | "ends"
  | "before" | "after" | "within" | "beyond" | "set" | "unset";

export interface FilterFieldSpec {
  id: string;
  label: string;
  hint: string;
  group: FilterFieldGroup;
  valueKind: FilterValueKind;
  operators: FilterOperator[];
  options: string[] | null;
}

export interface SortSpec {
  id: string;
  label: string;
  hint: string;
}

export interface PosterOptionSpec {
  id: string;
  label: string;
  description: string;
  defaultOn: boolean;
  /** Joins the single truncated line under the title rather than claiming a row. */
  line: boolean;
}

export interface LibraryControlSet {
  kind: MediaVariant;
  filterFields: FilterFieldSpec[];
  sortFields: SortSpec[];
  posterOptions: PosterOptionSpec[];
}

/** One question, asked of one field. */
export interface FilterCondition {
  field: string;
  operator: FilterOperator;
  values: string[];
}

/** How each group is titled and ordered in the editor. */
export const FILTER_GROUPS: Array<{ key: FilterFieldGroup; label: string; blurb: string }> = [
  { key: "title", label: "The title", blurb: "What it is." },
  // Named on screen because it is the axis Radarr states in its own dialog that
  // it cannot follow: "filters are available only for the properties of a movie,
  // they are not available for properties of the file(s) you may have".
  { key: "file", label: "The file you hold", blurb: "The copy on your disk, not the title." },
  { key: "time", label: "Time", blurb: "Relative, so a saved filter does not go stale." },
  { key: "decision", label: "What Deluno decided", blurb: "Its own reasoning, and nothing else can ask it." }
];

/** How an operator reads in a sentence. Never abbreviated — this is the control's whole label. */
export const OPERATOR_LABELS: Record<FilterOperator, string> = {
  in: "is any of",
  notin: "is none of",
  all: "includes all of",
  is: "is",
  isnot: "is not",
  min: "at least",
  max: "at most",
  has: "contains",
  nothas: "does not contain",
  starts: "starts with",
  ends: "ends with",
  before: "before",
  after: "after",
  within: "in the last",
  beyond: "not in the last",
  set: "has a value",
  unset: "has no value"
};

/** Whether the operator needs a value beside it at all. Mirrors `TakesValues` on the server. */
export function operatorTakesValues(operator: FilterOperator): boolean {
  return operator !== "set" && operator !== "unset";
}

/** Whether the value is a list you pick several of rather than one you type. */
export function isMultiValue(field: FilterFieldSpec, operator: FilterOperator): boolean {
  return (
    (field.valueKind === "quality" || field.valueKind === "genre" || field.valueKind === "enum") &&
    (operator === "in" || operator === "notin" || operator === "all")
  );
}

/**
 * The form a condition travels in: `field:operator:a|b|c`.
 *
 * Flat and readable rather than a JSON blob, because these end up in a URL people
 * bookmark, share and read. Pipes rather than commas because a genre may hold a
 * comma; the server splits on the first two colons only, so a Windows path
 * survives intact.
 */
export function encodeCondition(condition: FilterCondition): string {
  return condition.values.length === 0
    ? `${condition.field}:${condition.operator}`
    : `${condition.field}:${condition.operator}:${condition.values.join("|")}`;
}

export function decodeCondition(raw: string): FilterCondition | null {
  const first = raw.indexOf(":");
  if (first < 0) return null;
  const second = raw.indexOf(":", first + 1);
  const field = raw.slice(0, first);
  const operator = (second < 0 ? raw.slice(first + 1) : raw.slice(first + 1, second)) as FilterOperator;
  const values = second < 0 ? [] : raw.slice(second + 1).split("|").filter(Boolean);
  return field && operator ? { field, operator, values } : null;
}

/**
 * Whether a condition is finished being written.
 *
 * A row exists from the moment you pick a field, and it has no value yet. The
 * server refuses a condition it cannot answer — deliberately, because a silently
 * dropped filter is a shelf that looks narrowed and is not — so an unfinished
 * row must not be sent at all. It was, once: picking "Has a file" emptied the
 * whole shelf and put "Could not load the library" on screen, which is a 400
 * doing exactly its job on a request that should never have left.
 */
export function isCompleteCondition(condition: FilterCondition): boolean {
  return !operatorTakesValues(condition.operator)
    ? true
    : condition.values.length > 0 && condition.values.every((value) => value.trim() !== "");
}

/**
 * Writes the conditions onto a catalogue request. One `f` per question, and only
 * the ones that are actually asking something.
 */
export function applyConditions(params: URLSearchParams, conditions: FilterCondition[]) {
  for (const condition of conditions.filter(isCompleteCondition)) {
    params.append("f", encodeCondition(condition));
  }
}

/**
 * The value a field starts with when you add it.
 *
 * Only where there is an honest one: a switch is yes or no and has to be one of
 * them, so it starts at yes. Everything else starts empty — picking a quality
 * tier or a number for somebody would narrow the shelf to something nobody asked
 * for the instant the row appeared.
 */
export function initialValues(field: FilterFieldSpec, operator: FilterOperator): string[] {
  if (!operatorTakesValues(operator)) return [];
  if (field.valueKind === "boolean") return ["true"];
  return [];
}

/**
 * How many questions the shelf is being asked.
 *
 * Drives the number on the Filter button, so a narrowed shelf can never look like
 * an unnarrowed one — which is the way people lose half their library and
 * conclude Deluno has.
 */
export function conditionCount(conditions: FilterCondition[]): number {
  // Only the finished ones. A row still waiting for a value is narrowing
  // nothing, and a badge saying otherwise is the same lie in the other
  // direction.
  return conditions.filter(isCompleteCondition).length;
}

/** The condition as one readable line: "Quality is any of Remux 2160p, WEB 2160p". */
export function describeCondition(condition: FilterCondition, field: FilterFieldSpec | undefined): string {
  const label = field?.label ?? condition.field;
  const operator = OPERATOR_LABELS[condition.operator] ?? condition.operator;
  if (!operatorTakesValues(condition.operator)) return `${label} ${operator}`;
  const values = condition.values.join(", ");
  const unit = condition.operator === "within" || condition.operator === "beyond"
    ? ` day${condition.values[0] === "1" ? "" : "s"}`
    : field?.valueKind === "gigabytes" ? " GB"
    : field?.valueKind === "minutes" ? " min"
    : "";
  return `${label} ${operator} ${values}${unit}`;
}

/**
 * Reads the conditions back off a saved view.
 *
 * Three shapes have been stored in that column. An array is the browser rule
 * engine's `rulesJson` from before #302 and means nothing. An object with
 * `qualities` and `minSizeGb` is the nine-field record from #302 to #324, and is
 * migrated below rather than dropped — somebody's saved 4K view keeps working.
 * Anything unreadable is "no filters", never a partial set, because a view that
 * silently narrowed by half of what you saved would be worse than one that
 * narrowed by none of it.
 */
export function parseConditions(raw: string | null | undefined): FilterCondition[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw) as unknown;
    if (Array.isArray(parsed)) {
      // Either the legacy rule engine's rows (objects, meaningless) or the new
      // condition list (objects with `field`). Only the latter reads back.
      return parsed.filter(isCondition);
    }
    if (parsed && typeof parsed === "object") return migrateLegacyFilters(parsed as Record<string, unknown>);
    return [];
  } catch {
    return [];
  }
}

function isCondition(value: unknown): value is FilterCondition {
  const candidate = value as FilterCondition | null;
  return Boolean(
    candidate &&
    typeof candidate.field === "string" &&
    typeof candidate.operator === "string" &&
    Array.isArray(candidate.values)
  );
}

/** The nine-property record #324 replaced, turned into the conditions that mean the same thing. */
function migrateLegacyFilters(saved: Record<string, unknown>): FilterCondition[] {
  const conditions: FilterCondition[] = [];
  const list = (key: string) => (Array.isArray(saved[key]) ? (saved[key] as string[]).filter(Boolean) : []);
  const number = (key: string) => (typeof saved[key] === "number" ? String(saved[key]) : null);

  if (list("qualities").length) conditions.push({ field: "quality", operator: "in", values: list("qualities") });
  if (list("genres").length) conditions.push({ field: "genre", operator: "all", values: list("genres") });

  const ranges: Array<[string, string, FilterOperator]> = [
    ["minSizeGb", "size", "min"], ["maxSizeGb", "size", "max"],
    ["minYear", "year", "min"], ["maxYear", "year", "max"],
    ["minRuntime", "runtime", "min"], ["maxRuntime", "runtime", "max"],
    ["minRating", "rating", "min"]
  ];
  for (const [key, field, operator] of ranges) {
    const value = number(key);
    if (value !== null) conditions.push({ field, operator, values: [value] });
  }

  return conditions;
}

/**
 * The control set for a media kind, fetched once per session.
 *
 * Cached by kind because it is a declaration, not data: it changes when Deluno
 * is deployed, never while somebody is browsing. The genre list and the quality
 * ladder keep their own endpoints — those *are* data, and a stale copy of either
 * would offer a tier the ladder no longer has.
 */
const controlCache = new Map<MediaVariant, Promise<LibraryControlSet>>();

export function fetchLibraryControls(variant: MediaVariant): Promise<LibraryControlSet> {
  const cached = controlCache.get(variant);
  if (cached) return cached;

  const endpoint = variant === "movies" ? "/api/movies/controls" : "/api/series/controls";
  const request = fetchJson<LibraryControlSet>(endpoint).catch(() => EMPTY_CONTROLS(variant));
  controlCache.set(variant, request);
  return request;
}

/**
 * What a shelf offers when the server has not answered yet.
 *
 * Deliberately empty rather than a hard-coded fallback list. A fallback would be
 * the second copy this whole change exists to delete, and it would be the copy
 * nobody updates.
 */
const EMPTY_CONTROLS = (variant: MediaVariant): LibraryControlSet => ({
  kind: variant,
  filterFields: [],
  sortFields: [],
  posterOptions: []
});

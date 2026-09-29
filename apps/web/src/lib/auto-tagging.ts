/**
 * Auto-tagging rules (#1024): what a rule says, in plain words.
 *
 * A rule is some tags plus some conditions. Conditions of the same kind are
 * alternatives (any one will do); conditions of different kinds must all hold;
 * each can be turned around with "is not". The server owns the matching; this
 * file only names things and writes a rule out so a person can read it back.
 */

export type AutoTagConditionType =
  | "genre"
  | "monitored"
  | "originalLanguage"
  | "qualityProfile"
  | "rootFolder"
  | "seriesType"
  | "status"
  | "tag"
  | "year"
  | "keyword"
  | "runtime"
  | "studio"
  | "library"
  | "anime"
  | "certification"
  | "collection"
  | "has4kFile"
  | "requested4k"
  | "viaSeerr"
  | "sourceList";

export interface AutoTagCondition {
  type: AutoTagConditionType;
  negate: boolean;
  value: string;
}

export interface AutoTagRuleItem {
  id: string;
  name: string;
  tags: string[];
  removeAutomatically: boolean;
  /** "both", "movies" or "series". */
  appliesTo: string;
  isEnabled: boolean;
  specifications: AutoTagCondition[];
  createdUtc: string;
  updatedUtc: string;
}

export interface AutoTagRulePayload {
  name: string;
  tags: string[];
  removeAutomatically: boolean;
  appliesTo: string;
  isEnabled: boolean;
  specifications: AutoTagCondition[];
}

export const CONDITION_LABELS: Record<AutoTagConditionType, string> = {
  genre: "Genre",
  monitored: "Monitored",
  originalLanguage: "Original language",
  qualityProfile: "Quality profile",
  rootFolder: "Root folder",
  seriesType: "Series type",
  status: "Status",
  tag: "Tag",
  year: "Year",
  keyword: "Keyword",
  runtime: "Runtime (minutes)",
  studio: "Studio or network",
  library: "Library",
  anime: "Anime",
  certification: "Certification",
  collection: "Collection",
  has4kFile: "Has a 4K file",
  requested4k: "Requested in 4K",
  viaSeerr: "Requested through Seerr",
  sourceList: "Added by a list or collection"
};

/** The conditions Sonarr and Radarr have, then what only Deluno knows. */
export const CONDITION_TYPES: AutoTagConditionType[] = [
  "genre",
  "monitored",
  "originalLanguage",
  "qualityProfile",
  "rootFolder",
  "seriesType",
  "status",
  "tag",
  "year",
  "keyword",
  "runtime",
  "studio",
  "library",
  "anime",
  "certification",
  "collection",
  "has4kFile",
  "requested4k",
  "viaSeerr",
  "sourceList"
];

const YES_NO_TYPES = new Set<AutoTagConditionType>(["monitored", "anime", "has4kFile", "requested4k", "viaSeerr"]);
const RANGE_TYPES = new Set<AutoTagConditionType>(["year", "runtime"]);
const ADD_TIME_ONLY = new Set<AutoTagConditionType>(["requested4k", "viaSeerr", "sourceList"]);

export const isYesNoCondition = (type: AutoTagConditionType) => YES_NO_TYPES.has(type);
export const isRangeCondition = (type: AutoTagConditionType) => RANGE_TYPES.has(type);
/** Only an add knows these; an edit or a refresh leaves a rule that asks them alone. */
export const isAddTimeOnlyCondition = (type: AutoTagConditionType) => ADD_TIME_ONLY.has(type);

/** A series-only condition means nothing on a film. */
export function conditionAppliesTo(type: AutoTagConditionType, appliesTo: string): boolean {
  return type !== "seriesType" || appliesTo !== "movies";
}

export function scopeLabel(appliesTo: string): string {
  if (appliesTo === "movies") return "Movies";
  if (appliesTo === "series") return "TV shows";
  return "Movies and TV shows";
}

/** "2000-2010" from two boxes; either end may stay empty. */
export function joinRange(from: string, to: string): string {
  const start = from.trim();
  const end = to.trim();
  if (!start && !end) return "";
  if (start && start === end) return start;
  return `${start}-${end}`;
}

export function splitRange(value: string): { from: string; to: string } {
  const text = value.trim();
  const dash = text.indexOf("-");
  if (dash < 0) return { from: text, to: text };
  return { from: text.slice(0, dash).trim(), to: text.slice(dash + 1).trim() };
}

/** Whether the text is a whole number, or a range of them with either end left open. */
export function isValidRange(value: string): boolean {
  const text = value.trim();
  if (!text) return false;
  const { from, to } = splitRange(text);
  const numeric = /^\d+$/;
  if (from && !numeric.test(from)) return false;
  if (to && !numeric.test(to)) return false;
  if (!from && !to) return false;
  return !(from && to && Number(from) > Number(to));
}

function describeValue(condition: AutoTagCondition, names: { qualityProfile: (id: string) => string; library: (id: string) => string }): string {
  const { type, value } = condition;
  if (isYesNoCondition(type)) return value === "false" ? "no" : "yes";
  if (isRangeCondition(type)) {
    const { from, to } = splitRange(value);
    if (from && from === to) return from;
    if (from && to) return `${from} to ${to}`;
    if (from) return `${from} or later`;
    return `${to} or earlier`;
  }
  if (type === "qualityProfile") return names.qualityProfile(value);
  if (type === "library") return names.library(value);
  return value;
}

/** One condition as a sentence fragment: "Genre is Animation", "Year is not 2000 to 2010". */
export function describeCondition(
  condition: AutoTagCondition,
  names: { qualityProfile: (id: string) => string; library: (id: string) => string } = { qualityProfile: (id) => id, library: (id) => id }
): string {
  return `${CONDITION_LABELS[condition.type] ?? condition.type} ${condition.negate ? "is not" : "is"} ${describeValue(condition, names)}`;
}

/**
 * The whole rule read back: conditions of one kind joined with "or", different
 * kinds with "and", the way the server checks them.
 */
export function describeConditions(
  conditions: AutoTagCondition[],
  names?: { qualityProfile: (id: string) => string; library: (id: string) => string }
): string {
  const groups = new Map<AutoTagConditionType, AutoTagCondition[]>();
  for (const condition of conditions) groups.set(condition.type, [...(groups.get(condition.type) ?? []), condition]);
  const parts = [...groups.values()].map((group) => group.map((condition) => describeCondition(condition, names)).join(" or "));
  return parts.join(" and ");
}

export function emptyCondition(type: AutoTagConditionType = "genre"): AutoTagCondition {
  return { type, negate: false, value: isYesNoCondition(type) ? "true" : "" };
}

/** Why a rule cannot be saved yet, in plain words, or null when it can. */
export function ruleProblem(rule: { name: string; tags: string[]; specifications: AutoTagCondition[] }): Record<string, string> {
  const problems: Record<string, string> = {};
  if (!rule.name.trim()) problems.name = "Give the rule a name.";
  if (rule.tags.length === 0) problems.tags = "Choose at least one tag for the rule to give.";
  if (rule.specifications.length === 0) {
    problems.specifications = "Add at least one condition, or the rule would never match anything.";
  } else {
    for (const [index, condition] of rule.specifications.entries()) {
      if (isRangeCondition(condition.type) ? !isValidRange(condition.value) : !isYesNoCondition(condition.type) && !condition.value.trim()) {
        problems.specifications = isRangeCondition(condition.type)
          ? `Condition ${index + 1}: write a number, or a range like 2000-2010, -1999 or 2015-.`
          : `Condition ${index + 1} needs a value.`;
        break;
      }
    }
  }
  return problems;
}

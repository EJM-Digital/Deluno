import { fetchJson } from "./api/client";
import type { CustomFormatItem, GuideCustomFormat, GuideFormatBundle, GuidePackage } from "./api/types";

export type RuleMediaType = "movies" | "tv";

/**
 * TRaSH publishes a movies copy and a TV copy of most formats, under different
 * ids, with each copy written in its own app's terms. A Deluno extra has no
 * media type and serves both.
 */
export function guideFormatAppliesTo(format: GuideCustomFormat, mediaType: RuleMediaType): boolean {
  const mediaTypes = format.mediaTypes ?? [];
  return mediaTypes.length === 0 || mediaTypes.some((item) => (item === "tv" ? "tv" : "movies") === mediaType);
}

/** Matched by TRaSH's own specifications rather than by patterns Deluno wrote. */
export function isGenuineGuideFormat(format: GuideCustomFormat): boolean {
  return Boolean(format.sourceMatcherClauses?.length) && format.patterns.length === 0;
}

/**
 * Makes a rule from a guide format. Only the guide id is sent: the server
 * gives the rule the guide's own conditions. Screens used to write them
 * themselves, three different ways, and one of them made every TRaSH pattern
 * a required title match.
 */
export function createRuleFromGuide(format: GuideCustomFormat, mediaType: RuleMediaType, score: number): Promise<CustomFormatItem> {
  return fetchJson<CustomFormatItem>("/api/custom-formats", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name: format.name, mediaType, score, trashId: format.trashId, conditions: null, upgradeAllowed: true })
  });
}

export interface GuideBundleTarget {
  format: GuideCustomFormat;
  mediaType: RuleMediaType;
  score: number;
}

/**
 * The rules a preset or starter adds: each format it names, for each media
 * type that format serves within the preset's own. A "Movies and TV" preset
 * adds TRaSH's movies AMZN to movies and its TV AMZN to TV.
 */
export function guideBundleTargets(
  includes: GuideFormatBundle["includes"],
  bundleMediaType: string,
  guide: GuidePackage
): GuideBundleTarget[] {
  const scope: RuleMediaType[] = bundleMediaType === "movies" ? ["movies"] : bundleMediaType === "all" ? ["movies", "tv"] : ["tv"];
  return includes.flatMap((entry) => {
    const format = guide.customFormats.find((candidate) => candidate.trashId === entry.trashId);
    if (!format) return [];
    return scope
      .filter((mediaType) => guideFormatAppliesTo(format, mediaType))
      .map((mediaType) => ({ format, mediaType, score: entry.score ?? format.originalScore }));
  });
}

/** One TRaSH or Radarr/Sonarr specification, as stored on a rule. */
export interface RuleSpecification {
  name: string;
  implementation: string;
  negate: boolean;
  required: boolean;
  value: string;
}

/** The specifications a rule stores, or null when it stores Deluno conditions instead. */
export function readSpecifications(conditions: string | null | undefined): RuleSpecification[] | null {
  const trimmed = conditions?.trim();
  if (!trimmed?.startsWith("[")) return null;
  try {
    const parsed = JSON.parse(trimmed) as unknown[];
    if (!Array.isArray(parsed) || parsed.length === 0) return null;
    const specifications: RuleSpecification[] = [];
    for (const item of parsed) {
      if (!item || typeof item !== "object" || !("implementation" in item)) return null;
      const spec = item as { name?: string; implementation: string; negate?: boolean; required?: boolean; fields?: unknown };
      specifications.push({
        name: spec.name ?? "",
        implementation: spec.implementation,
        negate: Boolean(spec.negate),
        required: Boolean(spec.required),
        value: fieldValue(spec.fields)
      });
    }
    return specifications;
  } catch {
    return null;
  }
}

/** The specifications of a guide format, before any rule is made from it. */
export function guideSpecifications(format: GuideCustomFormat): RuleSpecification[] {
  return (format.sourceMatcherClauses ?? []).map((clause) => ({
    name: clause.name,
    implementation: clause.implementation,
    negate: clause.negate,
    required: clause.required,
    value: fieldValue(safeParse(clause.fieldsJson))
  }));
}

const SPECIFICATION_KINDS: Record<string, string> = {
  ReleaseTitleSpecification: "Release title",
  ReleaseGroupSpecification: "Release group",
  SourceSpecification: "Source",
  ResolutionSpecification: "Resolution",
  QualityModifierSpecification: "Quality modifier",
  LanguageSpecification: "Language",
  IndexerFlagSpecification: "Indexer flag",
  ReleaseTypeSpecification: "Release type",
  SizeSpecification: "Size",
  EditionSpecification: "Edition"
};

/** "Release title", "Source" — the kind of thing a specification looks at. */
export function specificationKind(implementation: string): string {
  return SPECIFICATION_KINDS[implementation] ?? implementation.replace(/Specification$/, "");
}

function fieldValue(fields: unknown): string {
  if (Array.isArray(fields)) {
    const named = fields.find((field) => field && typeof field === "object" && (field as { name?: string }).name === "value") as { value?: unknown } | undefined;
    return String(named?.value ?? (fields[0] as { value?: unknown } | undefined)?.value ?? "");
  }
  if (fields && typeof fields === "object" && "value" in fields) return String((fields as { value: unknown }).value);
  return "";
}

function safeParse(json: string): unknown {
  try {
    return JSON.parse(json);
  } catch {
    return null;
  }
}

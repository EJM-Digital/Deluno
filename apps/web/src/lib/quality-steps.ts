import type { CustomFormatItem, GuideCustomFormat, GuidePackage } from "./api";

/**
 * The seven questions a quality profile is, and which of Deluno's own nouns
 * each one owns.
 *
 * <p>#386: the Quality & Release area was six tabs of Deluno's vocabulary —
 * Quality Profiles, Size Rules, Release Preferences, Acquisition Rules — which
 * is Radarr's shape with better copy. Building a profile is really seven
 * answerable questions, and each maps onto a family the engine already has, so
 * the screen can mirror the model instead of flattening it.</p>
 *
 * <p><b>Step 1 is pinned first</b> because quality is the spine everything else
 * qualifies, and <b>step 7 is pinned last</b> because refusal is subtractive —
 * you cannot sensibly say what you never want before saying what you want. The
 * middle five are order-independent and are presented in the order a person
 * tends to care about them.</p>
 */
export interface QualityStep {
  readonly id: StepId;
  readonly number: number;
  /** The question, in the owner's words. Never a noun of Deluno's. */
  readonly question: string;
  /** One line under the question saying what answering it decides. */
  readonly purpose: string;
  /**
   * Guide categories this step owns. Every category is owned by exactly one
   * step — `every_guide_category_is_owned_by_exactly_one_step` holds it, because
   * a format reachable from no step is a field that silently disappeared.
   */
  readonly categories: readonly string[];
}

export type StepId =
  | "quality"
  | "size"
  | "picture"
  | "sound"
  | "groups"
  | "language"
  | "never";

export const QUALITY_STEPS: readonly QualityStep[] = [
  {
    id: "quality",
    number: 1,
    question: "How good, and when to stop?",
    purpose: "The tiers you will accept, best first, and the one that ends the search.",
    // `source` (Remux, Blu-ray, IMAX) says how good the master is, and `misc`
    // is Repack/Proper — a better copy of the same release. `edition` is the
    // one judgement call here: an Extended Edition is which cut rather than
    // how good, but it is a preference about the release you want and this is
    // the step that owns that question.
    categories: ["source", "misc", "edition"]
  },
  {
    id: "size",
    number: 2,
    question: "How big should a file of that quality be?",
    purpose: "Your own size for each tier you accept. The band behind each slider is where files of that tier normally land.",
    categories: []
  },
  {
    id: "picture",
    number: 3,
    question: "What should the picture be?",
    purpose: "HDR and Dolby Vision, and which video codec you prefer.",
    categories: ["hdr", "codec"]
  },
  {
    id: "sound",
    number: 4,
    question: "What should the sound be?",
    purpose: "Audio format and how many channels.",
    categories: ["audio", "channels"]
  },
  {
    id: "groups",
    number: 5,
    question: "Who do you want it from?",
    purpose: "Trusted release groups, and which streaming services you prefer.",
    // `anime` is here because its formats are group tiers — Anime BD Tier 01,
    // Anime WEB Tier 01, Anime Raws — and the handful that are not travel with
    // the releases those tiers describe.
    categories: ["groups", "streaming", "anime"]
  },
  {
    id: "language",
    number: 6,
    question: "Which language?",
    purpose: "Original audio, a dub, or a multi-language release.",
    categories: ["language"]
  },
  {
    id: "never",
    number: 7,
    question: "What do you never want?",
    purpose: "Releases Deluno should refuse outright, whatever else they offer.",
    categories: ["unwanted"]
  }
];

/** Every category the guide ships, so ownership can be checked against reality. */
export function guideCategories(guide: GuidePackage): string[] {
  return [...new Set((guide.customFormats ?? []).map((format) => format.category))].sort();
}

/** The categories no step claims. Empty is the only acceptable answer. */
export function orphanedCategories(guide: GuidePackage): string[] {
  const owned = new Set(QUALITY_STEPS.flatMap((step) => step.categories));
  return guideCategories(guide).filter((category) => !owned.has(category));
}

/**
 * The formats a step offers, for one media type.
 *
 * <p>Matched through the guide rather than the format's own name, so a format
 * renamed upstream keeps its step. A local custom format the guide has never
 * heard of has no category, and lands under step 7 — not as a guess about what
 * it means, but because a rule you wrote yourself is a rule you are asserting,
 * and step 7 is the one that shows assertions.</p>
 */
export function formatsForStep(
  step: QualityStep,
  formats: readonly CustomFormatItem[],
  guide: GuidePackage
): CustomFormatItem[] {
  const byTrashId = new Map<string, GuideCustomFormat>(
    (guide.customFormats ?? []).map((format) => [format.trashId, format])
  );

  return formats.filter((format) => {
    const guideFormat = format.trashId ? byTrashId.get(format.trashId) : undefined;
    return guideFormat
      ? step.categories.includes(guideFormat.category)
      : step.id === "never";
  });
}

/**
 * What a step's current answer reads as on the checklist.
 *
 * <p>A sentence, not a count. "3 selected" tells somebody they answered
 * something and nothing about what they answered, which is the failure the
 * whole redesign exists to fix.</p>
 */
export function describeAnswer(
  step: QualityStep,
  chosen: readonly string[],
  formats: readonly CustomFormatItem[]
): string {
  const names = chosen
    .map((id) => formats.find((format) => format.id === id)?.name)
    .filter((name): name is string => Boolean(name));

  if (names.length === 0) {
    return step.id === "never" ? "Nothing refused outright" : "No preference — anything is fine";
  }

  if (names.length <= 3) {
    return step.id === "never" ? `Never ${joinNames(names)}` : joinNames(names);
  }

  // Commas, not "and", once the list is cut short: "HDR10 and x265, and 2
  // more" reads as though the sentence restarted.
  const shown = names.slice(0, 2).join(", ");
  const rest = `${shown}, and ${names.length - 2} more`;
  return step.id === "never" ? `Never ${rest}` : rest;
}

function joinNames(names: readonly string[]): string {
  if (names.length === 1) return names[0];
  return `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
}

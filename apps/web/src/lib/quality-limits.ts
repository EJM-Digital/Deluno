/**
 * The plain choices behind a quality profile's score guardrails (#778, #938):
 *
 *   "Stop upgrading once a copy is: Good / Great / The best available"
 *   "Only replace when: A little better / Clearly better / Much better"
 *   "Never grab copies TRaSH rates as bad" (on/off)
 *
 * Numbers stay under the hood. The mapping mirrors
 * `Deluno.Quality.PlainQualityChoiceMapper` on the backend exactly - keep the
 * two in sync if either changes. It is duplicated here (rather than shared)
 * because the browser cannot call the C# mapper; the backend copy is the
 * source of truth and this one round-trips the same numbers it would produce.
 */

export type QualityCutoffChoice = "good" | "great" | "best";
export type QualityUpgradeGainChoice = "aLittle" | "clearly" | "much";

const GOOD_FRACTION = 0.25;
const GREAT_FRACTION = 0.6;
const CLEARLY_BETTER_MULTIPLIER = 3;
const MUCH_BETTER_MULTIPLIER = 6;

/** TRaSH's own defaults when a profile was never started from a guide plan. */
export const GENERIC_MIN_FORMAT_SCORE = 0;
export const GENERIC_CUTOFF_FORMAT_SCORE = 10000;
export const GENERIC_MIN_UPGRADE_FORMAT_SCORE = 1;

export function mapCutoffScore(choice: QualityCutoffChoice, guideMin: number, guideCutoff: number): number {
  const floor = Math.max(0, guideMin);
  const ceiling = Math.max(floor, guideCutoff);
  const span = ceiling - floor;
  if (choice === "good") return floor + Math.round(span * GOOD_FRACTION);
  if (choice === "great") return floor + Math.round(span * GREAT_FRACTION);
  return ceiling;
}

export function mapUpgradeGainScore(choice: QualityUpgradeGainChoice, guideMinUpgrade: number): number {
  const baseline = Math.max(1, guideMinUpgrade);
  if (choice === "aLittle") return baseline;
  if (choice === "clearly") return baseline * CLEARLY_BETTER_MULTIPLIER;
  return baseline * MUCH_BETTER_MULTIPLIER;
}

export function mapMinScore(neverGrabBad: boolean, guideMin: number): number | null {
  return neverGrabBad && guideMin > 0 ? guideMin : null;
}

export function nearestCutoffChoice(score: number, guideMin: number, guideCutoff: number): QualityCutoffChoice {
  const candidates: [QualityCutoffChoice, number][] = [
    ["good", mapCutoffScore("good", guideMin, guideCutoff)],
    ["great", mapCutoffScore("great", guideMin, guideCutoff)],
    ["best", mapCutoffScore("best", guideMin, guideCutoff)]
  ];
  return candidates.reduce((best, candidate) =>
    Math.abs(candidate[1] - score) < Math.abs(best[1] - score) ? candidate : best
  )[0];
}

export function nearestUpgradeGainChoice(score: number, guideMinUpgrade: number): QualityUpgradeGainChoice {
  const candidates: [QualityUpgradeGainChoice, number][] = [
    ["aLittle", mapUpgradeGainScore("aLittle", guideMinUpgrade)],
    ["clearly", mapUpgradeGainScore("clearly", guideMinUpgrade)],
    ["much", mapUpgradeGainScore("much", guideMinUpgrade)]
  ];
  return candidates.reduce((best, candidate) =>
    Math.abs(candidate[1] - score) < Math.abs(best[1] - score) ? candidate : best
  )[0];
}

export const CUTOFF_LABELS: Record<QualityCutoffChoice, string> = {
  good: "Good",
  great: "Great",
  best: "The best available"
};

export const UPGRADE_GAIN_LABELS: Record<QualityUpgradeGainChoice, string> = {
  aLittle: "A little better",
  clearly: "Clearly better",
  much: "Much better"
};

/** Plain labels for the language codes a profile can prefer (#769). */
export const LANGUAGE_LABELS: Record<string, string> = {
  original: "Original language",
  multi: "Multiple languages",
  dubbed: "A dub",
  en: "English",
  ja: "Japanese",
  de: "German",
  fr: "French",
  es: "Spanish"
};

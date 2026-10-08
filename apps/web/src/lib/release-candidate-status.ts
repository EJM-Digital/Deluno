/**
 * How a search candidate's decision status is shown to the owner.
 *
 * This lived twice, byte for byte, in the movie and show detail pages. That
 * is why the two pages could drift, and why a status added on the backend
 * would be handled on one screen and fall through to "Needs review" on the
 * other. One table, used by both.
 */

export interface ReleaseCandidateStatusInput {
  decisionStatus?: string;
  meetsCutoff: boolean;
  preferenceEvaluation?: unknown;
  preferenceComparison?: unknown;
}

export type CandidateTone = "ok" | "warn" | "bad";

export function isTypedCandidate(candidate: ReleaseCandidateStatusInput): boolean {
  return (candidate.preferenceEvaluation !== null && candidate.preferenceEvaluation !== undefined)
    || (candidate.preferenceComparison !== null && candidate.preferenceComparison !== undefined);
}

export function candidateLabel(candidate: ReleaseCandidateStatusInput): string {
  if (isTypedCandidate(candidate)) {
    switch (candidate.decisionStatus?.toLowerCase()) {
      case "rejected": return "Rejected";
      case "held": return "Needs review";
      case "equivalent": return "Equivalent";
      // Nothing rejected this release. Saying so would be untrue, and it
      // would hide the only fact that matters here: you already have better.
      case "current-better": return "Your file is better";
      // NOT "Best match". This chip says what Deluno thinks of one release,
      // and several releases can be preferred at once - five of them were, on
      // one search, each claiming to be the best. Which one Deluno would
      // actually take is a ranking, and the first row's caption says it.
      case "preferred": return "Preferred";
      case "acceptable": return "Acceptable";
      case "eligible": return "Eligible";
      default: return "Needs review";
    }
  }
  if (candidate.decisionStatus === "rejected") return "Rejected";
  if (["preferred", "eligible"].includes(candidate.decisionStatus || "") && candidate.meetsCutoff) return "Recommended";
  return "Needs review";
}

export function candidateTone(candidate: ReleaseCandidateStatusInput): CandidateTone {
  if (isTypedCandidate(candidate)) {
    const status = candidate.decisionStatus?.toLowerCase() ?? "";
    // "bad" is the tone for something being wrong. A release you simply do
    // not need is not wrong, so it reads as information, not as a failure.
    if (status === "rejected") return "bad";
    if (status === "equivalent" || status === "current-better") return "warn";
    return ["preferred", "acceptable"].includes(status) ? "ok" : "warn";
  }
  if (candidate.decisionStatus === "rejected") return "bad";
  if (["preferred", "eligible"].includes(candidate.decisionStatus || "") && candidate.meetsCutoff) return "ok";
  return "warn";
}

/**
 * Whether Deluno is arguing *for* this release. A drawer headed "Why Deluno
 * likes it" over a rejected release is telling the reader the opposite of
 * what the page just decided.
 */
export function likesCandidate(candidate: ReleaseCandidateStatusInput | null | undefined): boolean {
  if (!candidate) return true;
  const status = candidate.decisionStatus?.toLowerCase() ?? "";
  return status !== "rejected" && status !== "current-better" && status !== "equivalent";
}

/**
 * Whether this candidate could be the automatic winner. The "Best match"
 * caption on the first row is only true for a candidate that can actually
 * win; a list whose best row is one the installed file beats has no winner.
 */
export function canWinSearch(candidate: ReleaseCandidateStatusInput): boolean {
  const status = candidate.decisionStatus?.toLowerCase() ?? "";
  return status !== "rejected"
    && status !== "held"
    && status !== "equivalent"
    && status !== "current-better";
}

/**
 * One of the engine's sentences in the owner's words (#1138). A custom format that is "below its target" arrives as
 * "Custom rule · WEB Tier 01 is below its target.", a rule's internal name in front of a status nobody asked about.
 */
export function plainCandidateReason(text: string): string {
  return text
    // Searches saved before the wording was plain still carry the engine's own phrases (#1159).
    .replace(/Rejected by the typed release plan\./g, "Not taken.")
    .replace(/Preferred by the typed release plan\./g, "Would be taken.")
    .replace(/Held for review by the typed release plan\./g, "Held for your review.")
    .replace(/Equivalent to the installed file under the typed release plan\./g, "Same as the file you have.")
    .replace(/Eligible under the typed release plan\./g, "Usable, but not taken on its own.")
    .replace(/Candidate is acceptable but does not persistently improve the installed file\./g, "It isn't better than the file you have.")
    .replace(/Custom rule [·-] [^.]*? is below its target\./g, "It lacks something your quality profile prefers.")
    .replace(/(^|[.!?]\s+)?Custom rule [·-] [^.;']*Tier\s*\d+/gi, (_match, lead?: string) =>
      lead === undefined ? "your preferred release groups" : `${lead}Your preferred release groups`)
    .replace(/Custom rule [·-] /g, "");
}

function sentencesOf(text: string): string[] {
  return plainCandidateReason(text).split(/(?<=[.!?])\s+/).map((sentence) => sentence.trim()).filter(Boolean);
}

/**
 * What the release drawer says about a candidate, each thing once: the summary with its repeated sentences folded,
 * and the reasons that the summary does not already say (#1138).
 */
export function candidateExplanation(candidate: { summary?: string | null; decisionReasons?: string[] | null }): { summary: string; reasons: string[] } {
  const summarySentences = [...new Set(sentencesOf(candidate.summary ?? ""))];
  const said = new Set(summarySentences);
  const reasons: string[] = [];
  for (const raw of candidate.decisionReasons ?? []) {
    const reason = plainCandidateReason(raw).trim();
    if (!reason || said.has(reason) || reasons.includes(reason)) continue;
    reasons.push(reason);
  }
  return { summary: summarySentences.join(" "), reasons };
}

/** Sentences the engine writes for itself; they name no outcome a person can act on (#1159). */
const INTERNAL_SENTENCE = /typed preference evaluation|hybrid mode|ml boost|deterministic score/i;

/** What an episode's or title's last search came to, without the engine's own bookkeeping sentences. */
export function plainSearchResult(text: string): string {
  const kept = sentencesOf(text).filter((sentence) => !INTERNAL_SENTENCE.test(sentence));
  return [...new Set(kept)].join(" ");
}

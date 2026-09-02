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
      case "preferred": return "Best match";
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

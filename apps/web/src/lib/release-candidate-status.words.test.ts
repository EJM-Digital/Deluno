import { describe, expect, it } from "vitest";
import {
  candidateExplanation,
  candidateLabel,
  candidateTone,
  canWinSearch,
  isTypedCandidate,
  likesCandidate,
  plainCandidateReason,
  plainSearchResult
} from "./release-candidate-status";

const typedWith = (decisionStatus: string | undefined, meetsCutoff = true) => ({ decisionStatus, meetsCutoff, preferenceEvaluation: {} });
const plainWith = (decisionStatus: string | undefined, meetsCutoff: boolean) => ({ decisionStatus, meetsCutoff });

describe("which candidates carry a typed decision", () => {
  it("counts either a preference evaluation or a comparison, and neither as not typed", () => {
    expect(isTypedCandidate({ meetsCutoff: true, preferenceEvaluation: {} })).toBe(true);
    expect(isTypedCandidate({ meetsCutoff: true, preferenceComparison: {} })).toBe(true);
    expect(isTypedCandidate({ meetsCutoff: true, preferenceEvaluation: null, preferenceComparison: null })).toBe(false);
    expect(isTypedCandidate({ meetsCutoff: true })).toBe(false);
    expect(isTypedCandidate({ meetsCutoff: true, preferenceEvaluation: null, preferenceComparison: {} })).toBe(true);
    expect(isTypedCandidate({ meetsCutoff: true, preferenceEvaluation: {}, preferenceComparison: null })).toBe(true);
  });
});

describe("the label and colour of a typed candidate", () => {
  it.each([
    ["rejected", "Rejected", "bad"],
    ["held", "Needs review", "warn"],
    ["equivalent", "Equivalent", "warn"],
    ["current-better", "Your file is better", "warn"],
    ["preferred", "Preferred", "ok"],
    ["acceptable", "Acceptable", "ok"],
    ["eligible", "Eligible", "warn"],
    ["something-new", "Needs review", "warn"],
    ["PREFERRED", "Preferred", "ok"],
    ["REJECTED", "Rejected", "bad"]
  ])("%s reads %s in %s", (status, label, tone) => {
    expect(candidateLabel(typedWith(status))).toBe(label);
    expect(candidateTone(typedWith(status))).toBe(tone);
  });

  it("reads Needs review in the warning colour when it has no decision at all", () => {
    expect(candidateLabel(typedWith(undefined))).toBe("Needs review");
    expect(candidateTone(typedWith(undefined))).toBe("warn");
  });
});

describe("the label and colour of an older candidate with no typed decision", () => {
  it("calls a rejected one rejected", () => {
    expect(candidateLabel(plainWith("rejected", true))).toBe("Rejected");
    expect(candidateTone(plainWith("rejected", true))).toBe("bad");
  });

  it.each(["preferred", "eligible"])("recommends a %s one that meets the cutoff", (status) => {
    expect(candidateLabel(plainWith(status, true))).toBe("Recommended");
    expect(candidateTone(plainWith(status, true))).toBe("ok");
  });

  it.each(["preferred", "eligible"])("asks for a review of a %s one that falls short of the cutoff", (status) => {
    expect(candidateLabel(plainWith(status, false))).toBe("Needs review");
    expect(candidateTone(plainWith(status, false))).toBe("warn");
  });

  it("asks for a review of any other, or of one with no decision", () => {
    expect(candidateLabel(plainWith("held", true))).toBe("Needs review");
    expect(candidateTone(plainWith("held", true))).toBe("warn");
    expect(candidateLabel(plainWith(undefined, true))).toBe("Needs review");
    expect(candidateTone(plainWith(undefined, true))).toBe("warn");
  });
});

describe("whether Deluno is arguing for a release", () => {
  it("likes a release it has no candidate for, and every one that is not rejected, equal or beaten", () => {
    expect(likesCandidate(null)).toBe(true);
    expect(likesCandidate(undefined)).toBe(true);
    expect(likesCandidate({ meetsCutoff: true })).toBe(true);
    expect(likesCandidate(typedWith("preferred"))).toBe(true);
    expect(likesCandidate(typedWith("held"))).toBe(true);
  });

  it.each(["rejected", "current-better", "equivalent", "REJECTED"])("does not like a %s one", (status) => {
    expect(likesCandidate(typedWith(status))).toBe(false);
  });
});

describe("which release could win the search", () => {
  it.each(["rejected", "held", "equivalent", "current-better", "HELD"])("a %s one cannot", (status) => {
    expect(canWinSearch(typedWith(status))).toBe(false);
  });

  it("a preferred, acceptable or eligible one can, and so can one with no decision", () => {
    for (const status of ["preferred", "acceptable", "eligible", undefined]) expect(canWinSearch(typedWith(status))).toBe(true);
  });
});

describe("the engine's sentences in the owner's words", () => {
  it.each([
    ["Rejected by the typed release plan.", "Not taken."],
    ["Preferred by the typed release plan.", "Would be taken."],
    ["Held for review by the typed release plan.", "Held for your review."],
    ["Equivalent to the installed file under the typed release plan.", "Same as the file you have."],
    ["Eligible under the typed release plan.", "Usable, but not taken on its own."],
    ["Candidate is acceptable but does not persistently improve the installed file.", "It isn't better than the file you have."],
    ["Custom rule · WEB Tier 01 is below its target.", "It lacks something your quality profile prefers."],
    ["Custom rule - Group is below its target.", "It lacks something your quality profile prefers."],
    ["Custom rule · Releases from WEB Tier 02", "your preferred release groups"],
    ["Custom rule - Unwanted", "Unwanted"]
  ])("%s becomes %s", (said, words) => {
    expect(plainCandidateReason(said)).toBe(words);
  });

  it("starts a sentence with a capital when the tier rule opens it after another sentence", () => {
    expect(plainCandidateReason("Good. Custom rule · WEB Tier 01 matched.")).toBe("Good. Your preferred release groups matched.");
    expect(plainCandidateReason("Good! Custom rule · WEB Tier 3 matched")).toBe("Good! Your preferred release groups matched");
  });

  it("changes every occurrence, not just the first", () => {
    expect(plainCandidateReason("Rejected by the typed release plan. Rejected by the typed release plan.")).toBe("Not taken. Not taken.");
    expect(plainCandidateReason("Custom rule · A. Custom rule · B.")).toBe("A. B.");
  });

  it("leaves a sentence it does not know as it was said", () => {
    expect(plainCandidateReason("The size is wrong.")).toBe("The size is wrong.");
  });
});

describe("what the release drawer says about a candidate, each thing once", () => {
  it("folds repeated summary sentences and keeps the reasons the summary does not say", () => {
    const result = candidateExplanation({
      summary: "Preferred by the typed release plan. Preferred by the typed release plan. It has the audio you want.",
      decisionReasons: ["It has the audio you want.", "Would be taken.", "The size is fine.", "The size is fine.", "   ", "Eligible under the typed release plan."]
    });

    expect(result.summary).toBe("Would be taken. It has the audio you want.");
    expect(result.reasons).toEqual(["The size is fine.", "Usable, but not taken on its own."]);
  });

  it("has nothing to say when there is neither a summary nor reasons", () => {
    expect(candidateExplanation({})).toEqual({ summary: "", reasons: [] });
    expect(candidateExplanation({ summary: null, decisionReasons: null })).toEqual({ summary: "", reasons: [] });
  });
});

describe("what a title's last search came to", () => {
  it("drops the engine's own bookkeeping sentences and repeats", () => {
    const text = "Found 3 releases. Typed preference evaluation ran. Hybrid mode was on. ML boost applied. Deterministic score 5. Found 3 releases.";
    expect(plainSearchResult(text)).toBe("Found 3 releases.");
  });

  it("says nothing for an empty result", () => {
    expect(plainSearchResult("")).toBe("");
  });

  it("keeps what the search came to in the order it was said, one sentence after another", () => {
    expect(plainSearchResult("Found 3 releases. Best is Arrival.  Nothing else fits.")).toBe("Found 3 releases. Best is Arrival. Nothing else fits.");
    expect(plainSearchResult("  Found 3 releases.  ")).toBe("Found 3 releases.");
  });

  it("puts a capital on the preferred-groups wording after another sentence, however many spaces follow it, and for a tier written without a space", () => {
    expect(plainCandidateReason("Good.   Custom rule · WEB Tier 01 matched.")).toBe("Good.   Your preferred release groups matched.");
    expect(plainCandidateReason("Custom rule · WEB Tier01 matched.")).toBe("your preferred release groups matched.");
  });

  it("folds sentences split by more than one space and trims the ends", () => {
    expect(candidateExplanation({ summary: "  One.   Two.  One.  " })).toEqual({ summary: "One. Two.", reasons: [] });
  });
});

import { describe, expect, it } from "vitest";
import { candidateLabel, candidateTone, canWinSearch, isTypedCandidate } from "./release-candidate-status";

function typed(decisionStatus: string, meetsCutoff = true) {
  return { decisionStatus, meetsCutoff, preferenceComparison: {} };
}

describe("release candidate status", () => {
  it("does not call a release rejected when the installed file simply wins", () => {
    const candidate = typed("current-better");

    expect(candidateLabel(candidate)).toBe("Your file is better");
    expect(candidateLabel(candidate)).not.toContain("Rejected");
    expect(candidateTone(candidate)).not.toBe("bad");
  });

  it("keeps the failure tone for a release a hard rule actually rejected", () => {
    expect(candidateLabel(typed("rejected"))).toBe("Rejected");
    expect(candidateTone(typed("rejected"))).toBe("bad");
  });

  it("labels the remaining typed statuses in the owner's words", () => {
    expect(candidateLabel(typed("preferred"))).toBe("Best match");
    expect(candidateLabel(typed("acceptable"))).toBe("Acceptable");
    expect(candidateLabel(typed("eligible"))).toBe("Eligible");
    expect(candidateLabel(typed("equivalent"))).toBe("Equivalent");
    expect(candidateLabel(typed("held"))).toBe("Needs review");
  });

  it("only calls a row the best match when that row could actually win", () => {
    expect(canWinSearch(typed("preferred"))).toBe(true);
    expect(canWinSearch(typed("acceptable"))).toBe(true);
    expect(canWinSearch(typed("eligible"))).toBe(true);

    // None of these can be dispatched, so none of them is "Best match".
    expect(canWinSearch(typed("current-better"))).toBe(false);
    expect(canWinSearch(typed("equivalent"))).toBe(false);
    expect(canWinSearch(typed("held"))).toBe(false);
    expect(canWinSearch(typed("rejected"))).toBe(false);
  });

  it("leaves legacy score-based candidates on their original wording", () => {
    const legacy = { decisionStatus: "preferred", meetsCutoff: true };

    expect(isTypedCandidate(legacy)).toBe(false);
    expect(candidateLabel(legacy)).toBe("Recommended");
    expect(candidateTone(legacy)).toBe("ok");
    expect(candidateLabel({ decisionStatus: "rejected", meetsCutoff: false })).toBe("Rejected");
  });
});

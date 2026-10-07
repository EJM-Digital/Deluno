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
    expect(candidateLabel(typed("preferred"))).toBe("Preferred");
    expect(candidateLabel(typed("acceptable"))).toBe("Acceptable");
    expect(candidateLabel(typed("eligible"))).toBe("Eligible");
    expect(candidateLabel(typed("equivalent"))).toBe("Equivalent");
    expect(candidateLabel(typed("held"))).toBe("Needs review");
  });

  /**
   * A search returned five preferred candidates and the list put "Best match"
   * on all five. A superlative cannot be true of five things at once: the chip
   * classifies one release, and the ranking belongs to the row that leads.
   */
  it("does not let every preferred release call itself the best", () => {
    const five = [typed("preferred"), typed("preferred"), typed("preferred"), typed("preferred"), typed("preferred")];
    const labels = five.map(candidateLabel);

    expect(new Set(labels).size).toBe(1);
    expect(labels.every((label) => !label.toLowerCase().includes("best"))).toBe(true);
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

describe("candidate explanation (#1138)", () => {
  it("says a custom rule's shortfall once, in words, without the rule's name", async () => {
    const { candidateExplanation } = await import("./release-candidate-status");
    const sentence = "Custom rule · WEB Tier 01 is below its target.";
    const out = candidateExplanation({ summary: `Quality fine. ${sentence}`, decisionReasons: [sentence, "Seeders are fine."] });
    expect(out.summary).toBe("Quality fine. It lacks something your quality profile prefers.");
    expect(out.reasons).toEqual(["Seeders are fine."]);
    expect(JSON.stringify(out)).not.toContain("WEB Tier 01");
  });
});

describe("plain search result (#1159)", () => {
  it("drops the engine's own bookkeeping sentences and keeps what happened", async () => {
    const { plainSearchResult } = await import("./release-candidate-status");
    expect(plainSearchResult("Sent X to Transmission. Hybrid mode used deterministic score because ML boost was not applied.")).toBe("Sent X to Transmission.");
  });
});

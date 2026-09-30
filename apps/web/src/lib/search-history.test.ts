import { describe, expect, it } from "vitest";
import { parseSearchCandidates, searchCandidateCount } from "./search-history";

const name = (value: Record<string, unknown>) => String(value.ReleaseName ?? value.releaseName);

describe("a stored search's releases", () => {
  it("reads the automatic search's plan, where the rig stores them", () => {
    const details = JSON.stringify({
      EpisodeId: "e1",
      searchPlan: { BestCandidate: null, Candidates: [{ ReleaseName: "A" }, { ReleaseName: "B" }], CandidateCount: 40 }
    });
    expect(parseSearchCandidates(details, name)).toEqual(["A", "B"]);
    expect(searchCandidateCount(details)).toBe(40);
  });

  it("reads an older row that lists them at the top level", () => {
    const details = JSON.stringify({ candidates: [{ releaseName: "C" }] });
    expect(parseSearchCandidates(details, name)).toEqual(["C"]);
    expect(searchCandidateCount(details)).toBe(1);
  });

  it("answers nothing for no details or unreadable details", () => {
    expect(parseSearchCandidates(null, name)).toEqual([]);
    expect(parseSearchCandidates("not json", name)).toEqual([]);
    expect(searchCandidateCount("{}")).toBe(0);
  });
});

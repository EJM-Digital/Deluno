import { describe, expect, it } from "vitest";

import { describeSearchReason } from "./search-reasons";

describe("describeSearchReason", () => {
  it("explains why a partly installed season pack is held", () => {
    expect(describeSearchReason(
      "season_pack_replacement_requires_episode_scope",
      "fallback"
    )).toEqual({
      title: "Season upgrades need episode review",
      description: "This season already has episode files. Deluno held the whole-season replacement so each installed file can be compared under the current plan; search the selected episodes instead."
    });
  });

  it("explains evidence and per-episode comparison holds", () => {
    expect(describeSearchReason("season_pack_installed_evidence_missing", "fallback").title)
      .toBe("Installed episodes need file evaluation");
    expect(describeSearchReason("season_pack_candidate_not_upgrade_for_every_episode", "fallback").title)
      .toBe("The season pack would not improve every installed episode");
  });

  /**
   * A search that returned three candidates and held the best one for a person
   * reported "no_results", so the screen said the indexers had returned none.
   * Whether anything was found and whether anything was sent are two questions.
   */
  it("does not tell a person nothing was found when releases are waiting on them", () => {
    const held = describeSearchReason("held_for_review", "fallback");

    expect(held.title).not.toMatch(/no matching|nothing/i);
    expect(held.title).toMatch(/waiting on you/i);
    expect(held.description).toMatch(/compared/i);
    // And the genuinely empty case still says so.
    expect(describeSearchReason("no_results", "fallback").title).toMatch(/no matching/i);
  });
});

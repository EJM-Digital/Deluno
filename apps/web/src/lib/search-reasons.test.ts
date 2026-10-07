import { describe, expect, it } from "vitest";

import { describeSearchNow, describeSearchReason } from "./search-reasons";

describe("describeSearchReason", () => {
  it("says the indexers were busy rather than that they had nothing (#598)", () => {
    const explained = describeSearchReason("indexers_busy", "fallback");
    expect(explained.title).toBe("The indexers were busy");
    expect(explained.description).toMatch(/none was asked/);
  });

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

describe("describeSearchNow (#594)", () => {
  const release = { releaseName: "Breaking.Bad.S02E03.1080p.WEB-DL", indexerName: "Soak Torznab" };

  it("says a release was sent only when the client took it", () => {
    expect(describeSearchNow({ ...release, outcome: "matched", dispatchStatus: "sent" })).toEqual({
      tone: "success",
      title: "Sent Breaking.Bad.S02E03.1080p.WEB-DL via Soak Torznab to the download client.",
      description: undefined
    });
    expect(describeSearchNow({ ...release, outcome: "matched", dispatchStatus: "failed", dispatchMessage: "full" })?.tone).toBe("error");
  });

  it("calls a held release held, with Deluno's reason, never selected", () => {
    const notice = describeSearchNow({ ...release, outcome: "held", summary: "Equivalent to the installed file." });
    expect(notice).toEqual({
      tone: "info",
      title: "Held Breaking.Bad.S02E03.1080p.WEB-DL via Soak Torznab for review. Nothing was sent.",
      description: "Equivalent to the installed file."
    });
    expect(notice?.title).not.toMatch(/selected/i);
  });

  it("counts a show's missing episodes searched one by one (#593)", () => {
    expect(describeSearchNow({ outcome: "checked", plannedEpisodes: 67 })).toEqual({
      tone: "info",
      title: "Searching for 67 missing episodes, each on its own."
    });
    expect(describeSearchNow({ outcome: "checked", plannedEpisodes: 1 })?.title).toBe("Searching for 1 missing episode, each on its own.");
  });

  it("leaves an empty search to the reason text", () => {
    expect(describeSearchNow({ outcome: "checked" })).toBeNull();
  });
});

describe("posterSearchResult (#1156)", () => {
  it("says where a release went, or why nothing happened", async () => {
    const { posterSearchResult } = await import("./search-reasons");
    expect(posterSearchResult({ releaseName: "X", dispatchStatus: "sent", downloadClientName: "qBittorrent" }, false)).toBe("Sent to qBittorrent");
    expect(posterSearchResult({ outcome: "checked" }, true)).toBe("Nothing better found");
    expect(posterSearchResult({ outcome: "checked" }, false)).toBe("Nothing found yet");
  });
});

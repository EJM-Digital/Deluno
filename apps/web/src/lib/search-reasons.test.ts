import { describe, expect, it } from "vitest";

import { describeSearchNow, describeSearchReason, formatGrabMessage, formatSearchFailureNotice, posterSearchResult } from "./search-reasons";

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

describe("describeSearchReason, one assertion for each reason a search can give", () => {
  it.each([
    ["no_indexers", "No indexers are linked to this library", "Open Indexers", "/indexers/indexers"],
    ["all_indexers_failed", "Every indexer failed to answer", "Check indexers", "/indexers/indexers"],
    ["circuit_open", "Indexer search is temporarily paused", "Check indexers", "/indexers/indexers"],
    ["not_searchable", "This title is not linked to a searchable library", "Open Libraries", "/settings/libraries"],
    ["library_missing", "The linked library is missing", "Open Libraries", "/settings/libraries"]
  ])("%s says %s and links to %s", (reason, title, label, href) => {
    const explained = describeSearchReason(reason, "fallback");

    expect(explained.title).toBe(title);
    expect(explained.description).toBeTruthy();
    expect(explained.action).toEqual({ label, href });
  });

  it("no_usable_release says no release met the policy and links nowhere", () => {
    const explained = describeSearchReason("no_usable_release", "fallback");

    expect(explained.title).toBe("No release met the active policy");
    expect(explained.description).toMatch(/quality and custom-format rules/);
    expect(explained.action).toBeUndefined();
  });

  it("a reason it does not know is said in the caller's own words", () => {
    expect(describeSearchReason("something_new", "Nothing matched yet.")).toEqual({ title: "Nothing matched yet." });
    expect(describeSearchReason(undefined, "Search finished.")).toEqual({ title: "Search finished." });
  });
});

describe("formatSearchFailureNotice, the indexers that failed beside a usable answer", () => {
  const failure = (name: string, over: Record<string, unknown> = {}) =>
    ({ serviceType: "indexer", serviceId: name, serviceName: name, operation: "search", kind: "Timeout", retryState: "Retrying", message: "Timed out", code: null, httpStatus: null, upstreamDetail: null, externalId: null, retryAfterUtc: null, attempts: 1, isTransient: true, legacyCategory: "timeout", ...over }) as never;

  it("says nothing for no failures", () => {
    expect(formatSearchFailureNotice(undefined)).toBeUndefined();
    expect(formatSearchFailureNotice([])).toBeUndefined();
  });

  it("says the first failure in its own words with its next action when there is one", () => {
    expect(formatSearchFailureNotice([failure("Alpha", { summary: "Alpha search failed: Timed out.", nextAction: "Check Alpha's address." })])).toBe("Alpha search failed: Timed out. Check Alpha's address.");
  });

  it("builds a sentence from the service when the failure carries no summary", () => {
    expect(formatSearchFailureNotice([failure("Alpha")])).toBe("Alpha search failed: Timed out");
  });

  it("says one other source also needs attention for two failures", () => {
    expect(formatSearchFailureNotice([failure("Alpha", { summary: "Alpha failed." }), failure("Beta")])).toBe("Alpha failed. 1 other source also need attention.");
  });

  it("counts the others for three failures", () => {
    expect(formatSearchFailureNotice([failure("Alpha", { summary: "Alpha failed." }), failure("Beta"), failure("Gamma")])).toBe("Alpha failed. 2 other sources also need attention.");
  });
});

describe("what the title's Search now says about each answer", () => {
  it("a release the client refused is an error that says why", () => {
    const notice = describeSearchNow({ releaseName: "X.2024.1080p", indexerName: "Alpha", dispatchStatus: "failed", dispatchMessage: "Refused." });

    expect(notice).toMatchObject({ tone: "error" });
    expect(notice!.title).toBe("Matched X.2024.1080p via Alpha, but the download client rejected it: Refused.");
  });

  it("a release with no download address yet is information, not a send", () => {
    const notice = describeSearchNow({ releaseName: "X.2024.1080p", dispatchStatus: "planned" });

    expect(notice).toMatchObject({ tone: "info", title: "Matched X.2024.1080p, but no downloadable URL was available yet." });
  });

  it("a forced send reads Forced, a plain one reads Sent", () => {
    expect(formatGrabMessage("X", { forceOverride: true, dispatchStatus: "sent" })).toBe("Forced X to the download client.");
    expect(formatGrabMessage("X", { dispatchStatus: "sent" })).toBe("Sent X to the download client.");
  });

  it("a release found but not sent says nothing was sent", () => {
    expect(describeSearchNow({ releaseName: "X.2024.1080p" })).toMatchObject({ tone: "info", title: "Found X.2024.1080p. Nothing was sent." });
  });

  it("a show's episodes searched one by one are counted in the singular and the plural", () => {
    expect(describeSearchNow({ plannedEpisodes: 1 })!.title).toBe("Searching for 1 missing episode, each on its own.");
    expect(describeSearchNow({ plannedEpisodes: 3 })!.title).toBe("Searching for 3 missing episodes, each on its own.");
  });
});

describe("the short line on a poster after its Search icon ran", () => {
  it.each([
    [{ releaseName: "X", dispatchStatus: "sent", downloadClientName: "qBittorrent" }, false, "Sent to qBittorrent"],
    [{ releaseName: "X", dispatchStatus: "sent" }, false, "Sent to your download client"],
    [{ releaseName: "X", dispatchStatus: "failed" }, false, "Your download client refused it"],
    [{ releaseName: "X", outcome: "held" }, false, "Found one, waiting on you"],
    [{ releaseName: "X" }, false, "Found one, nothing sent"],
    [{ outcome: "checked" }, true, "Nothing better found"],
    [{ outcome: "checked" }, false, "Nothing found yet"]
  ])("%j (has a file: %s) reads %s", (payload, hasFile, line) => {
    expect(posterSearchResult(payload, hasFile)).toBe(line);
  });
});

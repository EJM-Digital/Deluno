import { describe, expect, it } from "vitest";
import { groupActivity, outcomeWords, sentenceOf } from "./group";
import type { LiveActivityRow } from "../live-events";
import type { TitleLookup } from "./titles";

const row = (id: string, message: string, extra: Partial<LiveActivityRow> = {}): LiveActivityRow =>
  ({ id, category: "episode.search.executed", message, createdUtc: "2026-10-01T00:00:00Z", relatedEntityType: "episode", relatedEntityId: id, severity: "info", fresh: false, ...extra }) as LiveActivityRow;
const episodes = new Map<string, { seriesId: string; season: number; episode: number }>([
  ["e1", { seriesId: "s1", season: 1, episode: 9 }],
  ["e2", { seriesId: "s1", season: 2, episode: 1 }],
  ["e3", { seriesId: "s1", season: 2, episode: 3 }]
]);
const titles: TitleLookup = { movies: new Map(), series: new Map([["s1", { title: "Lioness" }]]), episodes };
const when = (r: LiveActivityRow) => `at ${r.id}`;
const search = (id: string, code: string, outcome: string, extra: Partial<LiveActivityRow> = {}) => row(id, `Episode search executed: ${code} - ${outcome}`, extra);

describe("an episode search's outcome in a few words", () => {
  it.each([
    ["skipped", "nothing better than what you have"],
    ["  SKIPPED ", "nothing better than what you have"],
    ["queued", "found a release and sent it to your client"],
    ["Grabbed 1", "found a release and sent it to your client"],
    ["sent to qBittorrent", "found a release and sent it to your client"],
    ["no-results", "no release found"],
    ["no result", "no release found"],
    ["no_match", "no release found"],
    ["nomatch", "no release found"],
    ["no release", "no release found"],
    ["Rejected-by_rule", "rejected by rule"],
    ["not queued", "not queued"]
  ])("%s reads %s", (said, words) => {
    expect(outcomeWords(said)).toBe(words);
  });
});

describe("the sentence for one event", () => {
  it("leaves an event that is not an episode search exactly as it was said", () => {
    expect(sentenceOf(row("e1", "Grabbed Dune."), titles)).toBe("Grabbed Dune.");
  });

  it("names the show and the episode, or just the episode when the show is not known", () => {
    expect(sentenceOf(search("e1", "S01E09", "skipped"), titles)).toBe("Searched Lioness S01E09 — nothing better than what you have");
    expect(sentenceOf(search("zz", "S1E2", "queued"), titles)).toBe("Searched S01E02 — found a release and sent it to your client");
  });

  it("reads the search whatever the case or spacing", () => {
    expect(sentenceOf(row("e1", "episode search executed:s01e09 -  skipped"), titles)).toBe("Searched Lioness S01E09 — nothing better than what you have");
  });
});

describe("neighbouring events that say the same thing", () => {
  it("fold into a range across seasons, naming both ends", () => {
    const groups = groupActivity([search("e3", "S02E03", "skipped"), search("e2", "S02E01", "skipped"), search("e1", "S01E09", "skipped")], titles, when);
    expect(groups).toHaveLength(1);
    expect(groups[0].sentence).toBe("Searched Lioness S01E09–S02E03 — nothing better than what you have");
  });

  it("fold into a range inside a season with the last episode padded", () => {
    const groups = groupActivity([search("e3", "S02E03", "skipped"), search("e2", "S02E01", "skipped")], titles, when);
    expect(groups[0].sentence).toBe("Searched Lioness S02E01–E03 — nothing better than what you have");
  });

  it("say one episode once however many times it was searched", () => {
    const groups = groupActivity([search("e3", "S02E03", "skipped"), search("e3", "S02E03", "skipped")], titles, when);
    expect(groups).toHaveLength(1);
    expect(groups[0].sentence).toBe("Searched Lioness S02E03 — nothing better than what you have");
    expect(groups[0].rows).toHaveLength(2);
  });

  it("fold for an unnamed show without a name in the sentence", () => {
    const groups = groupActivity([search("x1", "S01E02", "skipped"), search("x2", "S01E01", "skipped")], titles, when);
    expect(groups).toHaveLength(1);
    expect(groups[0].sentence).toBe("Searched S01E01–E02 — nothing better than what you have");
  });

  it("do not fold when the outcome differs, or the show", () => {
    expect(groupActivity([search("e3", "S02E03", "skipped"), search("e2", "S02E01", "queued")], titles, when)).toHaveLength(2);
    expect(groupActivity([search("e3", "S02E03", "skipped"), search("x2", "S01E01", "skipped")], titles, when)).toHaveLength(2);
  });

  it("fold when other events say exactly the same in the same kind, and not otherwise", () => {
    const grabbed = (id: string, message: string, extra: Partial<LiveActivityRow> = {}) => row(id, message, { category: "movies.release.grabbed", relatedEntityType: "movie", ...extra });
    expect(groupActivity([grabbed("a", "Grabbed Dune."), grabbed("b", "Grabbed Dune.")], titles, when)).toHaveLength(1);
    expect(groupActivity([grabbed("a", "Grabbed Dune."), grabbed("b", "Grabbed Heat.")], titles, when)).toHaveLength(2);
    expect(groupActivity([grabbed("a", "Grabbed Dune."), grabbed("b", "Grabbed Dune.", { category: "movies.release.force-grabbed" })], titles, when)).toHaveLength(2);
    expect(groupActivity([grabbed("a", "Grabbed Dune."), grabbed("b", "Grabbed Dune.", { severity: "error" })], titles, when)).toHaveLength(2);
  });

  it("do not fold an ordinary event with a search that follows it", () => {
    const groups = groupActivity([row("a", "Grabbed Dune.", { category: "movies.release.grabbed" }), search("e3", "S02E03", "skipped")], titles, when);
    expect(groups).toHaveLength(2);
    const reversed = groupActivity([search("e3", "S02E03", "skipped"), row("a", "Grabbed Dune.", { category: "movies.release.grabbed" })], titles, when);
    expect(reversed).toHaveLength(2);
  });

  it("only merge neighbours, so the order of the day is kept", () => {
    const groups = groupActivity([search("e3", "S02E03", "skipped"), row("a", "Grabbed Dune.", { category: "movies.release.grabbed" }), search("e2", "S02E01", "skipped")], titles, when);
    expect(groups.map((g) => g.rows.length)).toEqual([1, 1, 1]);
  });
});

describe("each group's tooltip and freshness", () => {
  it("gives one line for each event with when it happened, newest first, and the newest row names the group", () => {
    const groups = groupActivity([search("e3", "S02E03", "skipped"), search("e2", "S02E01", "skipped")], titles, when);
    expect(groups[0].first.id).toBe("e3");
    expect(groups[0].detail).toEqual([
      "Searched Lioness S02E03 — nothing better than what you have (at e3)",
      "Searched Lioness S02E01 — nothing better than what you have (at e2)"
    ]);
  });

  it("is fresh when any of its events is, and not otherwise", () => {
    expect(groupActivity([search("e3", "S02E03", "skipped", { fresh: true }), search("e2", "S02E01", "skipped")], titles, when)[0].fresh).toBe(true);
    expect(groupActivity([search("e3", "S02E03", "skipped"), search("e2", "S02E01", "skipped", { fresh: true })], titles, when)[0].fresh).toBe(true);
    expect(groupActivity([search("e3", "S02E03", "skipped")], titles, when)[0].fresh).toBe(false);
  });

  it("makes a group of every kind of the last event too, with its detail", () => {
    const groups = groupActivity([row("a", "Grabbed Dune.", { category: "movies.release.grabbed" })], titles, when);
    expect(groups[0].detail).toEqual(["Grabbed Dune. (at a)"]);
    expect(groupActivity([], titles, when)).toEqual([]);
  });

  it("gives every group its tooltip and freshness, not only the last", () => {
    const groups = groupActivity([search("e3", "S02E03", "skipped", { fresh: true }), row("a", "Grabbed Dune.", { category: "movies.release.grabbed" })], titles, when);
    expect(groups[0].detail).toEqual(["Searched Lioness S02E03 — nothing better than what you have (at e3)"]);
    expect(groups[0].fresh).toBe(true);
    expect(groups[1].fresh).toBe(false);
  });

  it("does not fold a plain event into a search group just because it says the same sentence", () => {
    const sentence = "Searched Lioness S02E03 — nothing better than what you have";
    const groups = groupActivity([search("e3", "S02E03", "skipped"), row("a", sentence)], titles, when);
    expect(groups).toHaveLength(2);
  });
});

describe("reading the search message exactly", () => {
  it("turns runs of hyphens and underscores in an unknown outcome into single spaces", () => {
    expect(outcomeWords("a--b__c")).toBe("a b c");
  });

  it.each([
    "Prefix Episode search executed: S01E01 - skipped",
    "Episode search executed: S01E01 - skipped\nmore words",
    "Episode search executed: S01E01 skipped",
    "Episode search executed: S001E01 - skipped",
    "Episode search executed: S01E1234 - skipped",
    "Episode search executed: S01E01 -"
  ])("does not read %j as a search", (message) => {
    expect(sentenceOf(row("e1", message), titles)).toBe(message);
  });

  it("reads a search with no spaces around the hyphen or with several", () => {
    expect(sentenceOf(row("e1", "Episode search executed: S01E09-skipped"), titles)).toBe("Searched Lioness S01E09 — nothing better than what you have");
    expect(sentenceOf(row("e1", "Episode search executed:   S01E09   -   skipped"), titles)).toBe("Searched Lioness S01E09 — nothing better than what you have");
    expect(sentenceOf(row("e1", "Episode search executed:S01E09 - skipped"), titles)).toBe("Searched Lioness S01E09 — nothing better than what you have");
  });
});

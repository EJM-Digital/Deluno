import { describe, expect, it } from "vitest";
import { groupActivity, sentenceOf } from "./group";
import type { LiveActivityRow } from "../live-events";
import type { TitleLookup } from "./titles";

const row = (id: string, message: string, extra: Partial<LiveActivityRow> = {}): LiveActivityRow => ({ id, category: "episode.search.executed", message, createdUtc: "2026-10-01T00:00:00Z", relatedEntityType: "episode", relatedEntityId: id, fresh: false, ...extra });
const episodes = new Map<string, { seriesId: string; season: number; episode: number }>(["3", "4", "5", "6", "8"].map((n) => [`e${n}`, { seriesId: "s2", season: 2, episode: Number(n) }]));
episodes.set("e90", { seriesId: "s3", season: 1, episode: 1 });
const titles: TitleLookup = { movies: new Map(), series: new Map([["s2", { title: "Lioness" }], ["s3", { title: "Severance" }]]), episodes };
const ago = () => "1 min ago";

describe("grouping what Deluno did", () => {
  it("names the show and the outcome of one search", () => {
    expect(sentenceOf(row("e3", "Episode search executed: S02E03 - skipped"), titles)).toBe("Searched Lioness S02E03 — nothing better than what you have");
  });

  it("folds neighbouring searches for one show into a range with the count", () => {
    const groups = groupActivity(
      ["e8", "e6", "e5", "e4", "e3"].map((id) => row(id, `Episode search executed: S02E0${id.slice(1)} - skipped`)),
      titles,
      ago
    );
    expect(groups).toHaveLength(1);
    expect(groups[0].sentence).toBe("Searched Lioness S02E03–E08 — nothing better than what you have");
    expect(groups[0].rows).toHaveLength(5);
    expect(groups[0].detail[0]).toContain("S02E08");
  });

  it("keeps a different show, a different outcome and a different kind of event apart, in order", () => {
    const groups = groupActivity(
      [
        row("e4", "Episode search executed: S02E04 - skipped"),
        row("e90", "Episode search executed: S01E01 - skipped"),
        row("e5", "Episode search executed: S02E05 - queued"),
        row("g1", "Grabbed Dune.", { category: "movies.release.grabbed", relatedEntityType: "movie", relatedEntityId: "m1" })
      ],
      titles,
      ago
    );
    expect(groups.map((group) => group.sentence)).toEqual([
      "Searched Lioness S02E04 — nothing better than what you have",
      "Searched Severance S01E01 — nothing better than what you have",
      "Searched Lioness S02E05 — found a release and sent it to your client",
      "Grabbed Dune."
    ]);
  });

  it("folds identical sentences of the same kind, and only neighbours", () => {
    const same = (id: string) => row(id, "Removed a leftover download folder.", { category: "download.orphan.removed", relatedEntityType: null, relatedEntityId: null });
    const groups = groupActivity([same("a"), same("b"), row("x", "Grabbed Dune.", { category: "movies.release.grabbed" }), same("c")], titles, ago);
    expect(groups.map((group) => group.rows.length)).toEqual([2, 1, 1]);
  });
});

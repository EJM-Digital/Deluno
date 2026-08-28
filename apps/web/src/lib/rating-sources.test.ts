import { describe, expect, it } from "vitest";

import { adaptMovieItems } from "./ui-adapters";

/**
 * The four scores reach the browser separately.
 *
 * #319's complaint was that Deluno stored four and showed one blended number,
 * and that the adapter's per-source helpers "have never returned anything"
 * because they read a blob with the wrong keys. These are the record that each
 * source now arrives on its own — including the two that used to fall through
 * to the same default and were indistinguishable on screen.
 */
describe("per-source ratings", () => {
  const item = {
    id: "1",
    title: "Interstellar",
    releaseYear: 2014,
    monitored: true,
    hasFile: true,
    rating: 8.4,
    voteCount: 36000,
    ratings: [
      { source: "tmdb", label: "TMDb", score: 8.4, maxScore: 10, voteCount: 36000, url: null, kind: "community" },
      { source: "imdb", label: "IMDb", score: 8.7, maxScore: 10, voteCount: 2100000, url: null, kind: "community" },
      { source: "rotten_tomatoes", label: "Rotten Tomatoes", score: 73, maxScore: 100, voteCount: null, url: null, kind: "critic" },
      { source: "metacritic", label: "Metacritic", score: 74, maxScore: 100, voteCount: null, url: null, kind: "critic" }
    ]
  };

  it("keeps the four apart rather than blending them", () => {
    const [adapted] = adaptMovieItems([item as never]);

    // Four distinguishable numbers, so a source reading another's column fails
    // here rather than passing because everything happened to be 8.4.
    expect(adapted.tmdbRating).toBe(8.4);
    expect(adapted.imdbRating).toBe(8.7);
    expect(adapted.tomatoRating).toBe(73);
    expect(adapted.metacriticRating).toBe(74);
  });

  it("carries the vote counts the two community sources report", () => {
    const [adapted] = adaptMovieItems([item as never]);

    // A 9.4 from eleven votes is not a 9.4 — the count is what makes the score
    // filterable into something meaningful.
    expect(adapted.tmdbVotes).toBe(36000);
  });

  it("has no Trakt field, because Deluno has no Trakt source", () => {
    const [adapted] = adaptMovieItems([item as never]);

    // It used to, read from a metadata key nothing wrote, so it could only ever
    // be null. A field that is always empty is the same defect as a filter that
    // always matches nothing.
    expect("traktRating" in adapted).toBe(false);
  });
});

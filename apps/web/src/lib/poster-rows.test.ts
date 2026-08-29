import { describe, expect, it } from "vitest";

import { posterRowsFor } from "../components/app/library-grid";
import type { MediaItem } from "./media-types";

/**
 * A column under the poster means the same thing on every card.
 *
 * <p>This is the rule that got broken twice by hand. First the rows were joined
 * into one sentence; then they were split but dropped when a title had no value
 * for them, so a film with no file had its runtime where its neighbour had its
 * size — James: "the columns should mean the same thing for every card Im kind
 * of shocked why they dont?"</p>
 *
 * <p>Neither slipped past a browser sweep that measured row counts, alignment
 * and truncation, because both of those were <i>correct</i> in the broken
 * version. The only thing that catches it is asking what each row is.</p>
 */
describe("the rows under a poster", () => {
  const full: MediaItem = {
    id: "1",
    title: "Arrival",
    year: 2016,
    type: "movie",
    poster: null,
    backdrop: null,
    quality: "Bluray-1080p",
    monitored: true,
    sizeGb: 8,
    rating: 7.6,
    genres: ["Drama", "Science Fiction"],
    added: "Aug 26",
    overview: "",
    hasFile: true,
    runtimeMinutes: 116,
    releaseGroup: "SPARKS",
    codec: "H.264",
    audioCodec: "DTS-HD",
    tmdbRating: 7.6,
    inCinemas: "2016-11-10"
  } as MediaItem;

  /** The same title with nothing known about it — a fresh add, no file. */
  const bare: MediaItem = {
    ...full,
    id: "2",
    title: "Dune",
    hasFile: false,
    sizeGb: null,
    rating: null,
    genres: [],
    added: "",
    runtimeMinutes: null,
    releaseGroup: null,
    codec: null,
    audioCodec: null,
    tmdbRating: null,
    inCinemas: null
  } as MediaItem;

  const everything = {
    showRating: true,
    showSize: true,
    showRuntime: true,
    showGenres: true,
    showReleaseGroup: true,
    showCodec: true,
    showAdded: true,
    showRatingtmdb: true,
    showInCinemas: true
  };

  it("gives both cards the same rows in the same order", () => {
    const rich = posterRowsFor(full, everything).map((row) => row.option);
    const empty = posterRowsFor(bare, everything).map((row) => row.option);

    // Identical lists, so row four is the same fact on both cards. This is the
    // assertion that fails if anyone filters out the rows with no value again.
    expect(empty).toEqual(rich);
  });

  it("keeps a row for a switch this title has nothing for", () => {
    const rows = posterRowsFor(bare, everything);

    expect(rows).toHaveLength(Object.keys(everything).length);

    // Null, not absent. The card draws an em dash for it, which is what tells
    // you the switch is on and this title has none — the difference between
    // "nothing known" and "this control is broken".
    expect(rows.find((row) => row.option === "showSize")?.value).toBeNull();
    expect(rows.find((row) => row.option === "showRuntime")?.value).toBeNull();
  });

  it("draws nothing for a switch that is off", () => {
    const rows = posterRowsFor(full, { showRuntime: true });

    expect(rows.map((row) => row.option)).toEqual(["showRuntime"]);
    expect(rows[0].value).toBe("1h 56m");
  });

  it("reads every switch it is given", () => {
    const rows = posterRowsFor(full, everything);

    // Every one of these produced a value, so a switch that silently reads a
    // field nothing populates fails here rather than looking like a defect on
    // a library that happens to have no data for it.
    for (const row of rows) {
      expect(row.value, `${row.option} read nothing from a fully populated title`).not.toBeNull();
    }
  });
});

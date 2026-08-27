import { describe, expect, it } from "vitest";
import { TITLE_BUCKET_UNIVERSE, bucketLabel, buildJumpBuckets } from "./library-buckets";
import type { MediaItem } from "./media-types";

function title(overrides: Partial<MediaItem> & { title: string }): MediaItem {
  return {
    id: overrides.title,
    year: null,
    type: "movie",
    poster: null,
    backdrop: null,
    quality: null,
    monitored: true,
    sizeGb: null,
    rating: null,
    genres: [],
    added: "",
    overview: "",
    ...overrides
  };
}

describe("buildJumpBuckets under a title sort", () => {
  it("offers every letter, so an empty library still has a rail to aim at", () => {
    const buckets = buildJumpBuckets([], "title", "asc");

    expect(buckets.map((bucket) => bucket.label)).toEqual([...TITLE_BUCKET_UNIVERSE]);
    expect(buckets.every((bucket) => bucket.index === null && bucket.count === 0)).toBe(true);
  });

  it("points each letter at the first title under it and counts the run", () => {
    const buckets = buildJumpBuckets(
      [
        title({ title: "Alien" }),
        title({ title: "Aliens" }),
        title({ title: "Blade Runner" })
      ],
      "title",
      "asc"
    );

    const byLabel = new Map(buckets.map((bucket) => [bucket.label, bucket]));
    expect(byLabel.get("A")).toMatchObject({ index: 0, count: 2 });
    expect(byLabel.get("B")).toMatchObject({ index: 2, count: 1 });
    expect(byLabel.get("C")).toMatchObject({ index: null, count: 0 });
  });

  it("reverses the alphabet with the sort, because the rail follows the shelf", () => {
    const ascending = buildJumpBuckets([], "title", "asc").map((bucket) => bucket.label);
    const descending = buildJumpBuckets([], "title", "desc").map((bucket) => bucket.label);

    expect(ascending[0]).toBe("#");
    expect(descending[0]).toBe("Z");
    expect(descending).toEqual([...ascending].reverse());
  });

  it("puts digits, symbols and accented starts under one stop", () => {
    expect(bucketLabel(title({ title: "300" }), "title")).toBe("#");
    expect(bucketLabel(title({ title: "[REC]" }), "title")).toBe("#");
    expect(bucketLabel(title({ title: "Ödipus" }), "title")).toBe("#");
    expect(bucketLabel(title({ title: "amélie" }), "title")).toBe("A");
  });

  it("merges a letter that reappears rather than listing it twice", () => {
    // The shelf is sorted, so this should not happen — but a bucket that lost
    // its count to a duplicate label would silently under-report the library.
    const buckets = buildJumpBuckets(
      [title({ title: "Alien" }), title({ title: "Blade Runner" }), title({ title: "Arrival" })],
      "title",
      "asc"
    );

    const a = buckets.find((bucket) => bucket.label === "A");
    expect(a).toMatchObject({ index: 0, count: 2 });
    expect(buckets.filter((bucket) => bucket.label === "A")).toHaveLength(1);
  });
});

describe("buildJumpBuckets under the other orders", () => {
  it("takes its stops and their order from the shelf, not from a second rule", () => {
    const buckets = buildJumpBuckets(
      [
        title({ title: "Newest", year: 2024 }),
        title({ title: "Also new", year: 2021 }),
        title({ title: "Older", year: 1998 }),
        title({ title: "Undated", year: null })
      ],
      "year",
      "desc"
    );

    expect(buckets).toEqual([
      { label: "2020s", index: 0, count: 2 },
      { label: "1990s", index: 2, count: 1 },
      { label: "—", index: 3, count: 1 }
    ]);
  });

  it("does not band a title that has no file as though it were a small one", () => {
    expect(bucketLabel(title({ title: "Missing", hasFile: false, sizeGb: 0 }), "size")).toBe("—");
    expect(bucketLabel(title({ title: "Held", hasFile: true, sizeGb: 7.5 }), "size")).toBe("5–10 GB");
    expect(bucketLabel(title({ title: "Missing", hasFile: false, bitrateMbps: 0 }), "bitrate")).toBe("—");
  });

  it("names the ladder's own rungs under a quality sort", () => {
    expect(bucketLabel(title({ title: "A", currentQuality: "Bluray-2160p" }), "quality")).toBe("Bluray-2160p");
    expect(bucketLabel(title({ title: "B", currentQuality: null }), "quality")).toBe("—");
  });

  it("bands runtime, rating and popularity, and calls an absent value absent", () => {
    expect(bucketLabel(title({ title: "A", runtimeMinutes: 89 }), "runtime")).toBe("1–1½h");
    expect(bucketLabel(title({ title: "A", runtimeMinutes: 95 }), "runtime")).toBe("1½–2h");
    expect(bucketLabel(title({ title: "B", runtimeMinutes: null }), "runtime")).toBe("—");
    expect(bucketLabel(title({ title: "C", rating: 8.4 }), "rating")).toBe("8–9");
    expect(bucketLabel(title({ title: "D", rating: null }), "rating")).toBe("—");
    expect(bucketLabel(title({ title: "E", popularity: 250 }), "popularity")).toBe("100–1k");
    expect(bucketLabel(title({ title: "F", popularity: null }), "popularity")).toBe("—");
  });

  it("buckets Added by month, which is why the item carries the raw timestamp", () => {
    // `added` is formatted for a poster line and carries no year, so nothing
    // could group by it. `addedUtc` is the same moment, unrounded.
    expect(bucketLabel(title({ title: "A", addedUtc: "2026-08-12T04:00:00Z" }), "added"))
      .toBe(new Date("2026-08-12T04:00:00Z").toLocaleDateString([], { month: "short", year: "numeric" }));
    expect(bucketLabel(title({ title: "B", addedUtc: null }), "added")).toBe("—");
    expect(bucketLabel(title({ title: "C", addedUtc: "not a date" }), "added")).toBe("—");
  });
});

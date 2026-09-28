import { describe, expect, it } from "vitest";
import { knownStructuralMatch } from "./library-view";
import type { MediaItem } from "../../lib/media-types";

/**
 * #960 — on a chip/monitoring/library switch with nothing cached, titles
 * already on screen that are known to match the new query should stay
 * instead of the whole shelf blanking to a skeleton. `knownStructuralMatch`
 * is the predicate `library-view.tsx` filters the old rows through in that
 * moment; these tests are its contract.
 */
function makeItem(id: string, extra: Partial<MediaItem> = {}): MediaItem {
  return {
    id,
    title: `Title ${id}`,
    year: 2020,
    type: "movie",
    poster: null,
    backdrop: null,
    quality: null,
    monitored: true,
    sizeGb: null,
    rating: null,
    genres: [],
    added: "1 Jan",
    overview: "",
    libraryId: "lib-a",
    ...extra
  };
}

describe("knownStructuralMatch (#960)", () => {
  it("keeps a title whose stored status already carries the new chip's mark", () => {
    const covered = makeItem("covered", { wantedStatus: "covered" });
    expect(knownStructuralMatch(covered, "covered", "any", null)).toBe(true);
  });

  it("drops a title whose stored status does not carry the new chip's mark", () => {
    const missing = makeItem("missing", { wantedStatus: "missing" });
    expect(knownStructuralMatch(missing, "covered", "any", null)).toBe(false);
  });

  it("treats an unset status as Missing, same as the poster's own badge", () => {
    const bare = makeItem("bare");
    expect(knownStructuralMatch(bare, "missing", "any", null)).toBe(true);
    expect(knownStructuralMatch(bare, "covered", "any", null)).toBe(false);
  });

  it("the All chip keeps every title regardless of status", () => {
    const covered = makeItem("covered", { wantedStatus: "covered" });
    const upcoming = makeItem("upcoming", { wantedStatus: "upcoming" });
    expect(knownStructuralMatch(covered, "all", "any", null)).toBe(true);
    expect(knownStructuralMatch(upcoming, "all", "any", null)).toBe(true);
  });

  it("narrows on monitoring the same way it narrows on the chip", () => {
    const watched = makeItem("watched", { monitored: true });
    const ignored = makeItem("ignored", { monitored: false });
    expect(knownStructuralMatch(watched, "all", "monitored", null)).toBe(true);
    expect(knownStructuralMatch(ignored, "all", "monitored", null)).toBe(false);
    expect(knownStructuralMatch(ignored, "all", "unmonitored", null)).toBe(true);
  });

  it("narrows on the target library when one is selected", () => {
    const here = makeItem("here", { libraryId: "lib-a" });
    const elsewhere = makeItem("elsewhere", { libraryId: "lib-b" });
    expect(knownStructuralMatch(here, "all", "any", "lib-a")).toBe(true);
    expect(knownStructuralMatch(elsewhere, "all", "any", "lib-a")).toBe(false);
    // No library selected: nothing is excluded on that axis.
    expect(knownStructuralMatch(elsewhere, "all", "any", null)).toBe(true);
  });

  it("requires every known axis to agree at once", () => {
    const item = makeItem("both", { wantedStatus: "covered", monitored: true, libraryId: "lib-a" });
    expect(knownStructuralMatch(item, "covered", "monitored", "lib-a")).toBe(true);
    expect(knownStructuralMatch(item, "covered", "unmonitored", "lib-a")).toBe(false);
    expect(knownStructuralMatch(item, "missing", "monitored", "lib-a")).toBe(false);
    expect(knownStructuralMatch(item, "covered", "monitored", "lib-b")).toBe(false);
  });
});

import { describe, expect, it } from "vitest";
import { fileNameFromPath, parseDeletedLeftoverPaths, parseSeasonPackSkippedFiles } from "./season-pack-leftovers";

/**
 * #889: a season pack's own leftovers are read out of the completed
 * event's detailsJson (either casing a backend writer might use), and a
 * path already confirmed deleted drops off the list the moment a later
 * leftovers-deleted event names it - the way the problem actually clears
 * from the screen rather than only for the life of the browser tab.
 */
describe("parseSeasonPackSkippedFiles", () => {
  it("reads PascalCase Skipped entries off the completed event", () => {
    const detailsJson = JSON.stringify({
      Skipped: [
        { SourcePath: "/downloads/pack/Extra.mkv", Reason: "The filename does not resolve to one or more canonical episodes in this show." }
      ]
    });
    expect(parseSeasonPackSkippedFiles(detailsJson)).toEqual([
      { sourcePath: "/downloads/pack/Extra.mkv", reason: "The filename does not resolve to one or more canonical episodes in this show." }
    ]);
  });

  it("reads camelCase skipped entries too", () => {
    const detailsJson = JSON.stringify({
      skipped: [{ sourcePath: "/downloads/pack/S01E02.mkv", reason: "Matched to episode(s) S01E02 but not imported: corrupt." }]
    });
    expect(parseSeasonPackSkippedFiles(detailsJson)).toEqual([
      { sourcePath: "/downloads/pack/S01E02.mkv", reason: "Matched to episode(s) S01E02 but not imported: corrupt." }
    ]);
  });

  it("returns nothing for a missing, malformed, or unrelated payload", () => {
    expect(parseSeasonPackSkippedFiles(null)).toEqual([]);
    expect(parseSeasonPackSkippedFiles("not json")).toEqual([]);
    expect(parseSeasonPackSkippedFiles(JSON.stringify({ Files: [] }))).toEqual([]);
  });
});

describe("parseDeletedLeftoverPaths", () => {
  it("reads the paths a leftovers-deleted event confirmed removing", () => {
    const detailsJson = JSON.stringify({ Deleted: ["/downloads/pack/Extra.mkv"], Refused: [] });
    expect(parseDeletedLeftoverPaths(detailsJson)).toEqual(["/downloads/pack/Extra.mkv"]);
  });

  it("returns nothing for a missing or malformed payload", () => {
    expect(parseDeletedLeftoverPaths(null)).toEqual([]);
    expect(parseDeletedLeftoverPaths("not json")).toEqual([]);
  });
});

describe("fileNameFromPath", () => {
  it("takes the last segment off either separator", () => {
    expect(fileNameFromPath("C:\\Downloads\\pack\\Extra.mkv")).toBe("Extra.mkv");
    expect(fileNameFromPath("/downloads/pack/Extra.mkv")).toBe("Extra.mkv");
  });
});

describe("reading a season pack's leftovers, other shapes", () => {
  it("skips entries that are not records or name no file, and gives a reason when none was written", () => {
    const details = JSON.stringify({ Skipped: ["text", null, { reason: "no path" }, { SourcePath: "D:/in/Extras.mkv" }, { sourcePath: "D:/in/Sample.mkv", reason: "Too small." }] });
    expect(parseSeasonPackSkippedFiles(details)).toEqual([
      { sourcePath: "D:/in/Extras.mkv", reason: "Did not resolve to a canonical episode." },
      { sourcePath: "D:/in/Sample.mkv", reason: "Too small." }
    ]);
  });

  it("gives nothing when the skipped list or the deleted list is missing, not a list, or unreadable", () => {
    expect(parseSeasonPackSkippedFiles('{"skipped":"none"}')).toEqual([]);
    expect(parseSeasonPackSkippedFiles("not json")).toEqual([]);
    expect(parseSeasonPackSkippedFiles(null)).toEqual([]);
    expect(parseDeletedLeftoverPaths('{"deleted":"none"}')).toEqual([]);
    expect(parseDeletedLeftoverPaths("not json")).toEqual([]);
    expect(parseDeletedLeftoverPaths(undefined)).toEqual([]);
  });

  it("keeps only the text entries of a deleted list", () => {
    expect(parseDeletedLeftoverPaths('{"Deleted":["a.mkv",3,null,"b.mkv"]}')).toEqual(["a.mkv", "b.mkv"]);
  });

  it("names a file by its last part, and a path that is only separators as itself", () => {
    expect(fileNameFromPath("D:/in/Show/Extras.mkv")).toBe("Extras.mkv");
    expect(fileNameFromPath(String.raw`D:/in/Show\Extras.mkv`)).toBe("Extras.mkv");
    expect(fileNameFromPath("/in/Show/Extras.mkv/")).toBe("Extras.mkv");
    expect(fileNameFromPath("///")).toBe("///");
  });
});

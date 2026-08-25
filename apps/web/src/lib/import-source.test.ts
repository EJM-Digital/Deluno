import { describe, expect, it } from "vitest";
import { resolveImportSourcePath } from "./import-source";

describe("resolveImportSourcePath", () => {
  it("prefers the path reported by the download client", () => {
    expect(resolveImportSourcePath(
      { sourcePath: "C:/client/finished/movie.mkv", libraryId: "movies" },
      [{ id: "movies", downloadsPath: "C:/Deluno/Downloads/Movies" }]
    )).toBe("C:/client/finished/movie.mkv");
  });

  it("uses the matched library override when the client reports no path", () => {
    expect(resolveImportSourcePath(
      { sourcePath: null, libraryId: "anime" },
      [{ id: "anime", downloadsPath: "C:/Deluno/Downloads/Anime" }]
    )).toBe("C:/Deluno/Downloads/Anime");
  });

  it("does not fall back to a different library or a global folder", () => {
    expect(resolveImportSourcePath(
      { sourcePath: null, libraryId: "movies" },
      [{ id: "tv", downloadsPath: "C:/Deluno/Downloads/TV" }]
    )).toBeNull();
  });
});

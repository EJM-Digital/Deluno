import { describe, expect, it } from "vitest";
import { libraryPairPlan } from "./library-pair";

describe("libraryPairPlan (#1411)", () => {
  it("names the two libraries and puts them in Movies and TV subfolders of the one root", () => {
    expect(libraryPairPlan("Anime", "/media/anime")).toEqual([
      { mediaType: "movies", name: "Anime Movies", folder: "/media/anime/Movies" },
      { mediaType: "tv", name: "Anime TV", folder: "/media/anime/TV" }
    ]);
  });

  it("keeps a Windows root's own separator and ignores a trailing one", () => {
    expect(libraryPairPlan(" Anime ", "D:\\Media\\Anime\\").map((part) => part.folder)).toEqual(["D:\\Media\\Anime\\Movies", "D:\\Media\\Anime\\TV"]);
  });

  it("has nothing to show until there is a name and a folder", () => {
    expect(libraryPairPlan("", "")).toEqual([
      { mediaType: "movies", name: "", folder: "" },
      { mediaType: "tv", name: "", folder: "" }
    ]);
  });
});

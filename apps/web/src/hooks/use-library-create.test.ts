import { describe, expect, it } from "vitest";
import { parseIdPrefixedSearch } from "./use-library-create";

describe("parseIdPrefixedSearch (#792)", () => {
  it("recognizes a tmdb: prefix", () => {
    expect(parseIdPrefixedSearch("tmdb:157336")).toEqual({ source: "tmdb", id: "157336" });
  });

  it("recognizes an imdb: prefix", () => {
    expect(parseIdPrefixedSearch("imdb:tt0816692")).toEqual({ source: "imdb", id: "tt0816692" });
  });

  it("recognizes a tvdb: prefix", () => {
    expect(parseIdPrefixedSearch("tvdb:78804")).toEqual({ source: "tvdb", id: "78804" });
  });

  it("is case-insensitive on the prefix and trims surrounding whitespace", () => {
    expect(parseIdPrefixedSearch("  IMDB:tt0816692  ")).toEqual({ source: "imdb", id: "tt0816692" });
  });

  it("does not match an ordinary title, even one containing a colon", () => {
    expect(parseIdPrefixedSearch("Severance: Season One")).toBeNull();
  });

  it("does not match a bare id with no prefix", () => {
    expect(parseIdPrefixedSearch("157336")).toBeNull();
  });

  it("does not match a prefix with nothing after the colon", () => {
    expect(parseIdPrefixedSearch("tmdb:")).toBeNull();
  });
});

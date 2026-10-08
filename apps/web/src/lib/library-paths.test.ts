import { describe, expect, it } from "vitest";
import { comparablePath, downloadsPathProblem, samePath } from "./library-paths";

describe("downloadsPathProblem", () => {
  it("allows an empty folder, meaning the client's own", () => {
    expect(downloadsPathProblem("  ")).toBeNull();
  });

  it("allows full Windows, share and Linux paths", () => {
    expect(downloadsPathProblem("C:\\Downloads\\Completed")).toBeNull();
    expect(downloadsPathProblem("\\\\nas\\downloads")).toBeNull();
    expect(downloadsPathProblem("/data/downloads")).toBeNull();
  });

  it("names a relative path in plain words", () => {
    expect(downloadsPathProblem("Downloads\\Completed")).toContain("full path");
  });
});

describe("samePath", () => {
  it("treats both separators, case and a trailing slash alike on a Windows path", () => {
    expect(samePath("C:/Downloads/Completed/Movies", "C:\\Downloads\\Completed\\Movies\\")).toBe(true);
    expect(samePath("c:/downloads/completed/movies/", "C:\\Downloads\\Completed\\Movies")).toBe(true);
    expect(samePath("\\\\NAS\\Share\\TV", "//nas/share/tv/")).toBe(true);
  });

  it("keeps case on a Linux path and tells different folders apart", () => {
    expect(samePath("/data/Movies", "/data/movies")).toBe(false);
    expect(samePath("/data/movies/", "/data/movies")).toBe(true);
    expect(samePath("C:/Downloads/Completed/Movies", "C:/Downloads/Completed/TV")).toBe(false);
  });

  it("never matches a blank folder", () => {
    expect(samePath("", "")).toBe(false);
    expect(comparablePath(null)).toBe("");
  });
});

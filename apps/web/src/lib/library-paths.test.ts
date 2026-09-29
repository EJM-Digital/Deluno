import { describe, expect, it } from "vitest";
import { downloadsPathProblem } from "./library-paths";

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

import { describe, expect, it } from "vitest";
import { plainTitle } from "./plain-title";

describe("plainTitle", () => {
  it("reads plain text, numbers and arrays", () => {
    expect(plainTitle("a path")).toBe("a path");
    expect(plainTitle(42)).toBe("42");
    expect(plainTitle(["from ", 3])).toBe("from 3");
  });

  it("reads the words inside elements and fragments, so a value with a styled part still has its tooltip", () => {
    expect(plainTitle(<>Advanced source: <span className="font-mono">/downloads/Completed</span></>)).toBe("Advanced source: /downloads/Completed");
  });

  it("says nothing for an empty node", () => {
    expect(plainTitle(null)).toBeUndefined();
    expect(plainTitle(<span />)).toBeUndefined();
  });
});

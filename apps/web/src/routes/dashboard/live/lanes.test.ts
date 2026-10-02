import { describe, expect, it } from "vitest";
import { LANES } from "./lanes";

describe("the Pipeline stations open the matching Activity filter (#1114)", () => {
  it("every station a title is moving through opens In progress, and the library opens Done", () => {
    for (const stage of ["grabbed", "downloading", "processor", "importing"] as const) {
      expect(LANES[stage].to).toBe("/activity?status=in-progress");
    }
    expect(LANES.imported.to).toBe("/activity?status=done");
  });
});

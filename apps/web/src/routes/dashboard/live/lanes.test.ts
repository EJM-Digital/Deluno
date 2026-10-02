import { describe, expect, it } from "vitest";
import { LANES } from "./lanes";

describe("where the Pipeline stations go", () => {
  it("a station a download is moving through opens Transfers, where it can be paused or removed, and the library opens Activity's Done (James, 2 Oct 2026)", () => {
    for (const stage of ["grabbed", "downloading", "processor", "importing"] as const) {
      expect(LANES[stage].to).toBe("/queue");
    }
    expect(LANES.imported.to).toBe("/activity?status=done");
  });
});

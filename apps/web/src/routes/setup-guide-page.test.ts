import { describe, expect, it } from "vitest";
import { resolveDraftFormatGoal } from "./setup-guide-page";

describe("setup guide draft format goal (#731)", () => {
  it("keeps a saved anime draft instead of silently rewriting it to balanced", () => {
    expect(resolveDraftFormatGoal("anime")).toBe("anime");
  });

  it("round-trips every other goal the guide still offers", () => {
    expect(resolveDraftFormatGoal("simpleClean")).toBe("simpleClean");
    expect(resolveDraftFormatGoal("balanced")).toBe("balanced");
    expect(resolveDraftFormatGoal("homeTheater")).toBe("homeTheater");
    expect(resolveDraftFormatGoal("storageSaver")).toBe("storageSaver");
  });

  it("falls back to not-chosen for an empty or unrecognised draft value", () => {
    expect(resolveDraftFormatGoal("")).toBe("");
    expect(resolveDraftFormatGoal("some-retired-goal")).toBe("");
  });
});

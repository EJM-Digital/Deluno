import { describe, expect, it } from "vitest";
import { getEffectiveFormatGoal, resolveDraftFormatGoal } from "./setup-guide-page";

describe("setup guide draft format goal (#731)", () => {
  it("keeps a saved anime draft instead of silently rewriting it to balanced", () => {
    expect(resolveDraftFormatGoal("anime")).toBe("anime");
  });

  it("round-trips every other goal the guide still offers", () => {
    expect(resolveDraftFormatGoal("simpleClean")).toBe("simpleClean");
    expect(resolveDraftFormatGoal("balanced")).toBe("balanced");
    expect(resolveDraftFormatGoal("homeTheater")).toBe("homeTheater");
    expect(resolveDraftFormatGoal("storageSaver")).toBe("storageSaver");
    expect(resolveDraftFormatGoal("archive")).toBe("archive");
  });

  it("falls back to not-chosen for an empty or unrecognised draft value", () => {
    expect(resolveDraftFormatGoal("")).toBe("");
    expect(resolveDraftFormatGoal("some-retired-goal")).toBe("");
  });
});

describe("setup guide effective format goal per media type", () => {
  type Form = Parameters<typeof getEffectiveFormatGoal>[1];
  it("gives TV the anime goal and films Balanced, because the anime bundle is TV-only", () => {
    const form = { formatGoal: "anime" } as Form;
    expect(getEffectiveFormatGoal("tv", form)).toBe("anime");
    expect(getEffectiveFormatGoal("movies", form)).toBe("balanced");
  });

  it("uses the chosen goal for both media types otherwise", () => {
    const form = { formatGoal: "homeTheater" } as Form;
    expect(getEffectiveFormatGoal("tv", form)).toBe("homeTheater");
    expect(getEffectiveFormatGoal("movies", form)).toBe("homeTheater");
  });

  it("gives films the archive goal and TV Balanced, because archive-4k-remux is movies-only (B006)", () => {
    const form = { formatGoal: "archive" } as Form;
    expect(getEffectiveFormatGoal("movies", form)).toBe("archive");
    expect(getEffectiveFormatGoal("tv", form)).toBe("balanced");
  });
});

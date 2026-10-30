import { describe, expect, it } from "vitest";
import { heldQualityLabel, qualityLabel } from "./quality-label";

describe("the quality a title is shown with", () => {
  it("tidies separators so 'Bluray-1080p' and 'Bluray 1080p' read the same", () => {
    expect(qualityLabel("Bluray-1080p")).toBe("Bluray 1080p");
    expect(qualityLabel("  WEB__2160p  ")).toBe("WEB 2160p");
  });

  it("has no label for a missing or blank value", () => {
    expect(qualityLabel(null)).toBeNull();
    expect(qualityLabel(undefined)).toBeNull();
    expect(qualityLabel("")).toBeNull();
    expect(qualityLabel("   ")).toBeNull();
  });

  it("shows the quality a title holds, never the one it wants", () => {
    expect(heldQualityLabel({ currentQuality: "Remux-2160p", quality: "WEB 1080p", hasFile: true })).toBe("Remux 2160p");
    expect(heldQualityLabel({ currentQuality: "WEB 720p" })).toBe("WEB 720p");
  });

  it("shows nothing for a title with no file, or no held quality", () => {
    expect(heldQualityLabel({ currentQuality: "WEB 2160p", hasFile: false })).toBeNull();
    expect(heldQualityLabel({ quality: "WEB 2160p", hasFile: true })).toBeNull();
  });
});

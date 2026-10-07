import { describe, expect, it } from "vitest";
import { baselineStarterFor } from "./settings-profiles-page";

const starter = (id: string, label: string, mediaType: "movies" | "tv") => ({ id, label, mediaType }) as Parameters<typeof baselineStarterFor>[0][number];

describe("baselineStarterFor (#1139)", () => {
  const starters = [
    starter("web-1080p", "1080p Streaming", "movies"),
    starter("web-1080p-tv", "1080p Streaming", "tv"),
    starter("web-720p-tv", "720p Streaming", "tv")
  ];

  it("gives a custom profile of either media type a baseline template to take its formats from", () => {
    expect(baselineStarterFor(starters, "movies")?.id).toBe("web-1080p");
    expect(baselineStarterFor(starters, "tv")?.id).toBe("web-1080p-tv");
  });

  it("prefers a balanced template when the guide has one", () => {
    expect(baselineStarterFor([...starters, starter("bal", "Balanced", "tv")], "tv")?.id).toBe("bal");
  });
});

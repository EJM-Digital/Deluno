import { describe, expect, it } from "vitest";
import type { MediaSubtitleRow } from "./api";
import { subtitleSourceLabel, subtitleTypeLabel, timingStatusLabel } from "./subtitle-display";

const row = (over: Partial<MediaSubtitleRow> = {}): MediaSubtitleRow => ({
  language: "en", source: "fetched", forced: false, hearingImpaired: false, filePath: null,
  streamIndex: null, codec: null, provider: null, sourceKey: null, ...over
});

describe("subtitle words on a title page", () => {
  it("names the type: forced wins over hearing-impaired, otherwise plain", () => {
    expect(subtitleTypeLabel(row({ forced: true, hearingImpaired: true }))).toBe("Forced");
    expect(subtitleTypeLabel(row({ hearingImpaired: true }))).toBe("Hearing-impaired");
    expect(subtitleTypeLabel(row())).toBe("Plain");
  });

  it("says where a subtitle came from in plain words", () => {
    expect(subtitleSourceLabel("fetched")).toBe("Fetched");
    expect(subtitleSourceLabel("embedded")).toBe("Inside the video");
    expect(subtitleSourceLabel("external")).toBe("Found beside the video");
  });

  it("says what the last timing sync found", () => {
    expect(timingStatusLabel(row({ timingStatus: "moved" }))).toBe("Timing: adjusted");
    expect(timingStatusLabel(row({ timingStatus: "already_in_time" }))).toBe("Timing: in time");
    expect(timingStatusLabel(row({ timingStatus: "not_checked" }))).toBe("Timing: not checked");
    expect(timingStatusLabel(row({ timingStatus: null }))).toBe("Timing: never synced");
  });
});

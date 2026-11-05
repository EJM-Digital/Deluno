import { describe, expect, it } from "vitest";
import { groupActivity } from "./group";
import { NO_TITLES } from "./titles";
import type { LiveActivityRow } from "../live-events";

/** #1491: a strike added again and again on one download is one line that updates, not a line every check. */
const strike = (id: string, message: string): LiveActivityRow => ({ id, category: "download.health.strike", message, createdUtc: "2026-11-05T04:41:54Z", relatedEntityType: "movie", relatedEntityId: "m1", fresh: false });
const RELEASE = "RoboCop 1987 RM4K 1080p BluRay x264";
const ago = () => "now";

describe("strikes on one download", () => {
  it("collapse into one line that says the newest strike, with how many there were", () => {
    const groups = groupActivity([
      strike("s3", `${RELEASE}: Too slow, strike 3 of 3: stayed under 1000 KB/s`),
      strike("s2", `${RELEASE}: Too slow, strike 2 of 3: stayed under 1000 KB/s`),
      strike("s1", `${RELEASE}: Too slow, strike 1 of 3: stayed under 1000 KB/s`)
    ], NO_TITLES, ago);
    expect(groups).toHaveLength(1);
    expect(groups[0].sentence).toContain("strike 3 of 3");
    expect(groups[0].rows).toHaveLength(3);
  });

  it("stay apart for another download or another rule", () => {
    const groups = groupActivity([
      strike("a", `${RELEASE}: Too slow, strike 1 of 3: slow`),
      strike("b", `${RELEASE}: Dead torrent, strike 1 of 3: no peers`),
      strike("c", "Barbie 2023 BluRay: Too slow, strike 1 of 3: slow")
    ], NO_TITLES, ago);
    expect(groups).toHaveLength(3);
  });

  it("stay apart when the line is not in the strike form, since nothing says they are one download", () => {
    const groups = groupActivity([
      strike("a", "A strike was written one way"),
      strike("b", "A strike was written another way")
    ], NO_TITLES, ago);
    expect(groups).toHaveLength(2);
  });
});

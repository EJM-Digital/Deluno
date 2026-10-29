import { describe, expect, it } from "vitest";
import { hasNoSeeders, isUsenetProtocol, protocolGroup } from "./protocol";

describe("protocol helper (#1404)", () => {
  it.each(["newznab", "usenet", "NewzNab", " sabnzbd ", "nzbget"])("treats %s as Usenet", (value) => {
    expect(isUsenetProtocol(value)).toBe(true);
    expect(protocolGroup(value)).toBe("usenet");
  });
  it.each(["torznab", "rss", "tracker", "qbittorrent", "", null, undefined])("treats %s as torrent", (value) => {
    expect(isUsenetProtocol(value)).toBe(false);
    expect(protocolGroup(value)).toBe("torrent");
  });
  it("flags 0 seeders only on torrent results", () => {
    expect(hasNoSeeders("newznab", 0)).toBe(false);
    expect(hasNoSeeders("usenet", 0)).toBe(false);
    expect(hasNoSeeders("torznab", 0)).toBe(true);
    expect(hasNoSeeders("rss", 0)).toBe(true);
    expect(hasNoSeeders("torznab", 5)).toBe(false);
    expect(hasNoSeeders("torznab", null)).toBe(false);
  });
});

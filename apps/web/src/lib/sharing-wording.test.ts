import { describe, expect, it } from "vitest";
import { sharingText } from "./sharing-wording";

describe("sharingText (#1516)", () => {
  it("says seeders and peers, singular and plural right", () => {
    expect(sharingText(5, 2, null, false)).toBe("2 seeders · 5 peers");
    expect(sharingText(1, 1, null, false)).toBe("1 seeder · 1 peer");
    expect(sharingText(3, 0, null, false)).toBe("0 seeders · 3 peers");
  });
  it("adds the swarm when the client reports it", () => {
    expect(sharingText(5, 3, 41, false)).toBe("3 of 41 seeders · 5 peers");
    expect(sharingText(2, 1, 1, false)).toBe("1 of 1 seeder · 2 peers");
  });
  it("ignores a swarm figure smaller than what is connected", () => {
    expect(sharingText(5, 4, 1, false)).toBe("4 seeders · 5 peers");
  });
  it("keeps the short form when seeders are unknown, and connections for Usenet", () => {
    expect(sharingText(12, null, null, false)).toBe("12 peers");
    expect(sharingText(8, 3, 9, true)).toBe("8 connections");
    expect(sharingText(1, undefined, undefined, true)).toBe("1 connection");
  });
});

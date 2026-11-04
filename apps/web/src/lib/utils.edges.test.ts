import { describe, expect, it } from "vitest";
import { addressForOtherDevices, formatBytes, formatBytesFromGb, NO_SIZE, plainAddress } from "./utils";

describe("a size in bytes", () => {
  it.each([
    [0, "0 B"],
    [840, "840 B"],
    [1023, "1023 B"],
    [1024, "1.0 KB"],
    [1536, "1.5 KB"],
    [1_048_575, "1.0 MB"],
    [12.5 * 1024 * 1024, "12.5 MB"],
    [99.9 * 1024 * 1024, "99.9 MB"],
    [100 * 1024 * 1024, "100 MB"],
    [100.4 * 1024 * 1024, "100 MB"],
    [120 * 1024 ** 3, "120 GB"],
    [5 * 1024 ** 4, "5.0 TB"],
    [5000 * 1024 ** 4, "5000 TB"]
  ])("%d reads %s", (value, text) => {
    expect(formatBytes(value)).toBe(text);
  });

  it.each([[null], [undefined], [-1], [Number.NaN], [Number.POSITIVE_INFINITY]])("%s reads a dash", (value) => {
    expect(formatBytes(value as number | null | undefined)).toBe(NO_SIZE);
    expect(NO_SIZE).toBe("—");
  });
});

describe("a size in gigabytes", () => {
  it("shows one decimal and a unit, and Unknown for anything that is not a number", () => {
    expect(formatBytesFromGb(1.25)).toBe("1.3 GB");
    expect(formatBytesFromGb(-2)).toBe("-2.0 GB");
    expect(formatBytesFromGb(null)).toBe("Unknown");
    expect(formatBytesFromGb(undefined)).toBe("Unknown");
    expect(formatBytesFromGb(Number.NaN)).toBe("Unknown");
  });
});

describe("an IPv4 address written the IPv6 way", () => {
  it("is shown as people write it, whatever surrounds it", () => {
    expect(plainAddress("::ffff:192.168.1.20")).toBe("192.168.1.20");
    expect(plainAddress("  ::FFFF:10.0.0.1  ")).toBe("10.0.0.1");
  });

  it("leaves anything else alone", () => {
    expect(plainAddress("192.168.1.20")).toBe("192.168.1.20");
    expect(plainAddress("x::ffff:192.168.1.20")).toBe("x::ffff:192.168.1.20");
    expect(plainAddress("::ffff:192.168.1.20x")).toBe("::ffff:192.168.1.20x");
    expect(plainAddress("::ffff:192.168.1")).toBe("::ffff:192.168.1");
    expect(plainAddress("::ffff:1234.168.1.20")).toBe("::ffff:1234.168.1.20");
  });
});

describe("the address another device can use", () => {
  const lan = { host: "192.168.1.10", reason: "A LAN address of this computer" };
  const name = { host: "DESKTOP-1", reason: "This computer's name" };

  it.each(["", "  ", "localhost", "LOCALHOST", "deluno.localhost", "::1", "[::1]", "127.0.0.1", "127.1.2.3", "localhost:5099", "127.0.0.1:5099"])("never gives out %j", (host) => {
    expect(addressForOtherDevices(host, [lan, name])).toBe("192.168.1.10");
  });

  it.each(["mylocalhost.example", "notlocalhost", "1127.0.0.1", "127.0.0.1.example.com", "127.0.0"])("treats %s as a real address", (host) => {
    expect(addressForOtherDevices(host, [lan])).toBe(host);
  });

  it("keeps a real address as the browser used it, without its port and without spaces around it", () => {
    expect(addressForOtherDevices("  deluno.home:7879  ", [lan])).toBe("deluno.home");
    expect(addressForOtherDevices("a:b:7879", [lan])).toBe("a:b:7879");
    expect(addressForOtherDevices("host:79x", [lan])).toBe("host:79x");
  });

  it("will not give out a loopback address that was detected as the LAN address or the name", () => {
    expect(addressForOtherDevices("localhost", [{ host: "127.0.0.1", reason: lan.reason }, name])).toBe("DESKTOP-1");
    expect(addressForOtherDevices("localhost", [{ host: "localhost", reason: name.reason }])).toBeNull();
  });

  it("uses a LAN address before the computer's name, and ignores a reason it does not know", () => {
    expect(addressForOtherDevices("localhost", [name, lan])).toBe("192.168.1.10");
    expect(addressForOtherDevices("localhost", [{ host: "10.0.0.5", reason: "Something else" }])).toBeNull();
    expect(addressForOtherDevices("localhost", [{ host: "10.0.0.5", reason: lan.reason.replace("A LAN", "a LAN") }])).toBeNull();
    expect(addressForOtherDevices("localhost", [{ host: "10.0.0.5", reason: "Another computer's name" }])).toBeNull();
  });
});

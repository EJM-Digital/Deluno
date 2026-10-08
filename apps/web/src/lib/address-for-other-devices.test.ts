import { describe, expect, it } from "vitest";
import { addressForOtherDevices } from "./utils";

const detected = [
  { host: "localhost", reason: "Always answers from this machine" },
  { host: "127.0.0.1", reason: "Always answers from this machine" },
  { host: "DESKTOP-1", reason: "This computer's name" },
  { host: "192.168.1.10", reason: "A LAN address of this computer" }
];

describe("the address other devices should open (#1141)", () => {
  it("never tells another device to open localhost: it uses this computer's LAN address", () => {
    expect(addressForOtherDevices("localhost", detected)).toBe("192.168.1.10");
    expect(addressForOtherDevices("127.0.0.1", detected)).toBe("192.168.1.10");
    expect(addressForOtherDevices("::1", detected)).toBe("192.168.1.10");
  });

  it("keeps the address the browser used when it is already a real network address", () => {
    expect(addressForOtherDevices("192.168.1.10", detected)).toBe("192.168.1.10");
    expect(addressForOtherDevices("::ffff:192.168.1.20", detected)).toBe("192.168.1.20");
    expect(addressForOtherDevices("deluno.home:7879", detected)).toBe("deluno.home");
  });

  it("falls back to the computer's name, then to no address at all", () => {
    expect(addressForOtherDevices("localhost", detected.filter((item) => item.reason !== "A LAN address of this computer"))).toBe("DESKTOP-1");
    expect(addressForOtherDevices("localhost", detected.slice(0, 2))).toBeNull();
  });
});

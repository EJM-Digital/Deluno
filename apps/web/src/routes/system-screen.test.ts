import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * System's "This installation" card used to echo settings.hostBindAddress /
 * settings.hostPort / settings.urlBase as "Listening on" and "URL base",
 * values nothing ever reads back once Kestrel is up (#736). It now reports
 * the effective port and whether other devices can connect, from the same
 * /api/system/lan-access data LanAccessNotice already uses.
 */
describe("system screen runtime card", () => {
  const source = readFileSync(resolve(__dirname, "system-screen.tsx"), "utf8");

  it("no longer reports the stored host bind address, port or URL base as if they were live", () => {
    expect(source).not.toMatch(/settings\.hostBindAddress/);
    expect(source).not.toMatch(/settings\.hostPort/);
    expect(source).not.toMatch(/settings\.urlBase/);
  });

  it("reports reachability from the lan-access endpoint", () => {
    expect(source).toMatch(/lan-access/);
    expect(source).toMatch(/lanAccess\?\.port/);
    expect(source).toMatch(/lanAccess\?\.state/);
  });
});

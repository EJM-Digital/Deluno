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

/**
 * #869: uptime, process start, OS/platform, .NET runtime version, database
 * type/location, and the app-data directory, read live from
 * /api/system/info rather than hardcoded at build time.
 */
describe("system screen installation card uptime/runtime/app-data (#869)", () => {
  const source = readFileSync(resolve(__dirname, "system-screen.tsx"), "utf8");

  it("reads uptime, runtime, OS, database, and app-data from /api/system/info", () => {
    expect(source).toMatch(/\/api\/system\/info/);
    expect(source).toMatch(/systemInfo\?\.osDescription/);
    expect(source).toMatch(/systemInfo\?\.runtimeVersion/);
    expect(source).toMatch(/systemInfo\?\.appDataPath/);
    expect(source).toMatch(/systemInfo\.uptimeSeconds/);
    expect(source).toMatch(/systemInfo\.databaseLocation/);
  });
});

/**
 * #870: the Updates card shows the changelog for a pending update and the
 * last applied updates, both read from UpdateStatusResponse rather than
 * hardcoded or backfilled.
 */
describe("system screen updates card release notes and history (#870)", () => {
  const source = readFileSync(resolve(__dirname, "system-screen.tsx"), "utf8");

  it("shows the changelog for an available update and the applied-update history", () => {
    expect(source).toMatch(/current\.changelog/);
    expect(source).toMatch(/current\.appliedUpdates/);
    expect(source).toMatch(/entry\.version/);
    expect(source).toMatch(/entry\.appliedAtUtc/);
  });
});

/**
 * #871: the Activity timeline's page-size control and Clear action, replacing
 * the fixed maxVisible={500} cap, with a confirmation before the destructive
 * clear since it also wipes decision-explanation history in the same table.
 */
describe("system screen activity timeline page size and clear (#871)", () => {
  const source = readFileSync(resolve(__dirname, "system-screen.tsx"), "utf8");

  it("no longer hardcodes maxVisible to 500 and offers a page-size control", () => {
    expect(source).not.toMatch(/maxVisible=\{500\}/);
    expect(source).toMatch(/activityPageSize/);
    expect(source).toMatch(/\/api\/activity\/display-settings/);
  });

  it("clears through a confirmation naming the decision-history consequence", () => {
    expect(source).toMatch(/\/api\/activity\/clear/);
    expect(source).toMatch(/confirmClearActivity/);
    expect(source).toMatch(/decision explanation/);
  });
});

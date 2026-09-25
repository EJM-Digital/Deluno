import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * Bind address, Port and URL base used to be editable fields on this page
 * that PATCHed host.bindAddress / host.port / host.urlBase, and nothing ever
 * read them back: Kestrel binds from Server:Port / Server:AllowLan
 * configuration in Deluno.Host/Program.cs, and there is no UsePathBase for a
 * URL base anywhere (#736). The page now shows the effective port and
 * whether other devices can connect, read from the real
 * /api/system/lan-access endpoint, and says plainly that the port and LAN
 * access are changed outside the app.
 */
describe("general settings page", () => {
  const source = readFileSync(resolve(__dirname, "settings-general-page.tsx"), "utf8");

  it("no longer offers editable bind address, port or URL base fields", () => {
    expect(source).not.toMatch(/label="Bind address"/);
    expect(source).not.toMatch(/label="Port"/);
    expect(source).not.toMatch(/label="URL base"/);
  });

  it("never sends hostBindAddress, hostPort or urlBase in its settings patch", () => {
    expect(source).not.toMatch(/hostBindAddress:/);
    expect(source).not.toMatch(/hostPort:/);
    expect(source).not.toMatch(/urlBase:/);
  });

  it("reads the effective port and reachability from the real lan-access endpoint", () => {
    expect(source).toMatch(/\/api\/system\/lan-access/);
    expect(source).toMatch(/status\.port/);
    expect(source).toMatch(/status\?\.state/);
  });

  it("says the port and LAN access change outside the app, not on this page", () => {
    expect(source).toMatch(/Server:Port/);
    expect(source).toMatch(/Server:AllowLan/);
  });
});

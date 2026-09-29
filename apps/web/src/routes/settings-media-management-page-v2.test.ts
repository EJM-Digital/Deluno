import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * The global "Remove empty folders after import" switch (CleanupEmptyFolders)
 * was stored and never read: import only ever consulted the per-library
 * LibraryItem.RemoveEmptySourceFolders switch (#736). If this switch comes
 * back on this page without a reader wired up, it is dead again.
 */
describe("import policy page", () => {
  const source = readFileSync(resolve(__dirname, "settings-media-management-page-v2.tsx"), "utf8");

  it("offers no global empty-folder cleanup switch", () => {
    expect(source).not.toMatch(/cleanupEmptyFolders/);
    expect(source).not.toMatch(/Remove empty folders after import/);
  });

  it("never sends cleanupEmptyFolders in its settings patches", () => {
    expect(source).not.toMatch(/cleanupEmptyFolders:/);
  });

  it("says empty-folder cleanup is a per-library choice instead", () => {
    expect(source).toMatch(/set per library/);
  });
});

/** #1055: a processor connection's health-check address is editable from its drawer, and the row points at it. */
describe("processor connection health-check address", () => {
  const source = readFileSync(resolve(__dirname, "settings-media-management-page-v2.tsx"), "utf8");

  it("has an Edit button per connection that opens the drawer and saves with PUT", () => {
    expect(source).toMatch(/aria-label=\{`Edit \$\{connection\.name\}`\}/);
    expect(source).toMatch(/method: editing \? "PUT" : "POST"/);
    expect(source).toMatch(/healthCheckUrl: connection\.healthCheckUrl \?\? ""/);
    expect(source).toMatch(/label="Health check address"/);
  });

  it("tells a degraded connection with no address where to add one", async () => {
    const { processorSub } = await import("./settings-media-management-page-v2");
    expect(processorSub({ secretConfigured: true, healthCheckUrl: null, healthStatus: "degraded" })).toMatch(/Add a health check address \(Edit\)/);
    expect(processorSub({ secretConfigured: true, healthCheckUrl: "http://x/ready", healthStatus: "healthy" })).toBe("Token saved");
    expect(processorSub({ secretConfigured: false, healthCheckUrl: null, healthStatus: "healthy" })).toBe("No token");
  });
});

/** #1077: a library links to its processor by id, so renaming the connection changes nothing. */
describe("a library's processor link", () => {
  const connections = [{ id: "c1", name: "Media cleaner" }] as never[];

  it("finds the processor by id after it has been renamed", async () => {
    const { findLinkedProcessor } = await import("./settings-media-management-page-v2");
    expect(findLinkedProcessor(connections, "c1", "Weir")).toMatchObject({ id: "c1" });
  });

  it("uses the name only when the library was never linked to a connection", async () => {
    const { findLinkedProcessor } = await import("./settings-media-management-page-v2");
    expect(findLinkedProcessor(connections, null, "Media cleaner")).toMatchObject({ id: "c1" });
    expect(findLinkedProcessor(connections, null, "Weir")).toBeNull();
    expect(findLinkedProcessor(connections, "gone", "Media cleaner")).toBeNull();
  });

  it("saves the id, not the name, as the link", () => {
    const source = readFileSync(resolve(__dirname, "settings-media-management-page-v2.tsx"), "utf8");
    expect(source).toMatch(/processorConnectionId: workflow\.processorConnectionId \|\| null/);
    expect(source).not.toMatch(/value=\{connection\.name\}/);
  });
});

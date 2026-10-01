import { describe, expect, it } from "vitest";
import { buildConnections, connKey, type ConnResult } from "./connections-model";

const indexer = (over: Record<string, unknown> = {}) =>
  ({ id: "i1", name: "NZBgeek", isEnabled: true, healthStatus: "healthy", lastHealthMessage: null, lastHealthLatencyMs: 200, lastHealthTestUtc: "2026-10-01T09:00:00Z", ...over }) as never;

function rowFor(results: ReadonlyMap<string, ConnResult>, over: Record<string, unknown> = {}) {
  return buildConnections({ indexers: [indexer(over)], clients: [], clientRows: [], connections: [], metadataMs: null, results, now: Date.parse("2026-10-01T10:00:00Z") })[0]!;
}

describe("connection rows carry the real last check (#1098)", () => {
  it("a newer check shown for a connection wins, and an older seeded one never covers a fresher reading", () => {
    const newer = new Map<string, ConnResult>([[connKey("indexer", "i1"), { state: "down", message: "Timed out.", checkedUtc: "2026-10-01T09:30:00Z" }]]);
    const older = new Map<string, ConnResult>([[connKey("indexer", "i1"), { state: "down", message: "Timed out.", checkedUtc: "2026-10-01T08:00:00Z" }]]);

    expect(rowFor(newer)).toMatchObject({ state: "down", sub: "Timed out.", checkedUtc: "2026-10-01T09:30:00Z" });
    expect(rowFor(older)).toMatchObject({ state: "ok", checkedUtc: "2026-10-01T09:00:00Z" });
  });

  it("a connection that has never been checked takes the first check it is given", () => {
    const first = new Map<string, ConnResult>([[connKey("indexer", "i1"), { state: "ok", message: "Answered.", checkedUtc: "2026-10-01T09:45:00Z" }]]);

    expect(rowFor(first, { lastHealthTestUtc: null, healthStatus: "unknown" })).toMatchObject({ state: "ok", checkedUtc: "2026-10-01T09:45:00Z" });
  });

  it("a switched-off connection is never given a result", () => {
    const any = new Map<string, ConnResult>([[connKey("indexer", "i1"), { state: "ok", message: "x", checkedUtc: "2026-10-01T09:45:00Z" }]]);

    expect(rowFor(any, { isEnabled: false }).state).toBe("off");
  });
});

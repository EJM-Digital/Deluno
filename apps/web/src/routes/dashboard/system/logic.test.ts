import { describe, expect, it } from "vitest";
import { areaOf, fullIn, mbps, sizeText, splitAddress, tracePaths, wholeRows } from "./logic";
import { buildConnections, connectionsLine, groupConnections } from "./connections-model";

describe("System logic", () => {
  it("puts checks in their area", () => {
    expect(areaOf({ id: "x", category: "indexer", name: "Nyaa.si" })).toBe("connections");
    expect(areaOf({ id: "x", category: "system", name: "A drive is nearly full" })).toBe("storage");
    expect(areaOf({ id: "x", category: "system", name: "The computer will sleep" })).toBe("machine");
    expect(areaOf({ id: "x", category: "system", name: "Deluno has no password" })).toBe("security");
  });
  it("words readings plainly", () => {
    expect(sizeText(387 * 1024 * 1024)).toBe("387 MB");
    expect(mbps(3.4e6)).toBe("3.4");
    expect(splitAddress("http://192.168.1.51:5099/")).toEqual({ host: "192.168.1.51", port: "5099" });
    expect(fullIn(null)).toBeNull();
    expect(fullIn(45)).toBe("45 days");
    expect(tracePaths([1], 10)).toBeNull();
    expect(wholeRows(100, 42, 5)).toBe(2);
  });
  it("groups connections worst first and says how many answer", () => {
    const rows = buildConnections({
      indexers: [
        { id: "a", name: "A", isEnabled: true, healthStatus: "healthy" },
        { id: "b", name: "B", isEnabled: true, healthStatus: "unreachable", lastHealthMessage: "no answer" },
        { id: "c", name: "C", isEnabled: false, healthStatus: "unknown" }
      ] as never,
      clients: [], clientRows: [], connections: null, metadataMs: 100, results: new Map(), now: 0
    });
    const groups = groupConnections(rows);
    expect(groups[0].rows.map((r) => r.name)).toEqual(["B", "A", "C"]);
    expect(groups[0].tone).toBe("red");
    expect(groups[0].badge).toBe("1/2 OK");
    expect(connectionsLine(rows)).toBe("2/3 answering · 1 down");
  });
});

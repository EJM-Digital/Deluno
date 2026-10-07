import { describe, expect, it } from "vitest";
import { indexerReadiness } from "./format";

/** #1134: the header counts only indexers that are on, tested healthy and used by a library, and says what it left out. */
describe("indexerReadiness", () => {
  const item = (id: string, healthStatus: string, isEnabled = true) => ({ id, healthStatus, isEnabled });

  it("does not count degraded, untested, unrouted or paused indexers as ready", () => {
    const used = new Set(["a", "b", "c"]);
    const result = indexerReadiness(
      [item("a", "healthy"), item("b", "degraded"), item("c", "untested"), item("d", "healthy"), item("e", "healthy", false)],
      (id) => (used.has(id) ? 1 : 0),
      2
    );
    expect(result.ready).toBe(1);
    expect(result.total).toBe(5);
    expect(result.notReady).toEqual(["1 degraded", "1 not tested yet", "1 not used by any library", "1 paused"]);
  });

  it("does not ask for a library when there are none yet", () => {
    expect(indexerReadiness([item("a", "healthy")], () => 0, 0).ready).toBe(1);
  });
});

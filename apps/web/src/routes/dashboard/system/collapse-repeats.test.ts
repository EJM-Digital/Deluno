import { describe, expect, it } from "vitest";
import { collapseRepeats } from "./bottom";
import type { SystemLogEntry } from "../../../lib/api/types/system-log";

const line = (id: string, message: string, level: SystemLogEntry["level"] = "warning"): SystemLogEntry =>
  ({ id, timeUtc: "2026-10-01T11:59:00Z", level, message, source: "Test" }) as SystemLogEntry;

describe("collapseRepeats", () => {
  it("joins back-to-back repeats into one counted line", () => {
    const out = collapseRepeats([line("1", "A failed."), line("2", "A failed.")]);
    expect(out.map((l) => [l.row.id, l.count])).toEqual([["1", 2]]);
  });

  it("joins repeats with other lines between them, so the sentence is never said twice", () => {
    const out = collapseRepeats([line("1", "A failed."), line("2", "B failed."), line("3", "A failed.")]);
    expect(out.map((l) => [l.row.message, l.count])).toEqual([["A failed.", 2], ["B failed.", 1]]);
  });

  it("keeps the same message at a different level apart", () => {
    expect(collapseRepeats([line("1", "A", "warning"), line("2", "A", "error")])).toHaveLength(2);
  });
});

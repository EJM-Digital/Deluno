import { describe, expect, it } from "vitest";
import { describeCleanup, type RecycleBinCleanupPreview } from "./recycle-bin-words";

/**
 * What emptying the recycle bin would take, said before it takes it.
 *
 * <p>DESIGN-007 decision 15: "Enforce retention automatically, and show what a
 * manual empty takes". The empty used to delete first and count afterwards,
 * which is a report rather than a choice — and permanent deletion is the one
 * place a report after the fact is worth nothing.</p>
 */
describe("what a manual empty takes", () => {
  const bytes = (value: number) => `${value} B`;

  it("says how many have expired and what it frees", () => {
    expect(describeCleanup(preview({ items: 3, expiredCount: 3 }), bytes)).toBe(
      "3 items have passed their retention date. Emptying frees 900 B. This cannot be undone."
    );
  });

  it("counts one as one", () => {
    expect(describeCleanup(preview({ items: 1, expiredCount: 1 }), bytes)).toContain(
      "1 item has passed its retention date"
    );
  });

  /**
   * The one that matters. An item that has not expired is going because the
   * bin is full — it is the only kind somebody might still have wanted back,
   * and a single total would bury it inside a number that reads as routine.
   */
  it("names the ones that have not expired first, and says why they are going", () => {
    const said = describeCleanup(preview({ items: 5, expiredCount: 3, overCapacityCount: 2 }), bytes);

    expect(said).toMatch(/^2 items that have not expired yet will go too, because the bin is over its size limit\./);
    expect(said).toContain("3 items have passed their retention date");
  });

  it("says it cannot be undone, whichever kind is going", () => {
    expect(describeCleanup(preview({ items: 1, expiredCount: 1 }), bytes)).toContain("cannot be undone");
    expect(describeCleanup(preview({ items: 1, overCapacityCount: 1 }), bytes)).toContain("cannot be undone");
  });

  /// Nothing to take reads as "nothing has expired", not as a broken button.
  it("explains an empty that would take nothing", () => {
    const said = describeCleanup(preview({ items: 0 }), bytes);

    expect(said).toContain("within its size limit");
    expect(said).not.toContain("cannot be undone");
  });

  function preview(overrides: {
    items: number;
    expiredCount?: number;
    overCapacityCount?: number;
  }): RecycleBinCleanupPreview {
    return {
      items: Array.from({ length: overrides.items }, (_, index) => ({ id: `item-${index}` })),
      expiredCount: overrides.expiredCount ?? 0,
      overCapacityCount: overrides.overCapacityCount ?? 0,
      bytesFreed: overrides.items * 300
    };
  }
});

import { describe, expect, it } from "vitest";
import { resolveDrawerSaveState, shouldClearSaveState } from "./drawer-save-state";

describe("resolveDrawerSaveState", () => {
  it("shows a refusal even though the form is still dirty", () => {
    // The case that was invisible on the rig: creating a library whose root is
    // a file returns HTTP 400, and the form is necessarily still dirty at that
    // moment, because the content that failed is still in it.
    expect(resolveDrawerSaveState("error", true)).toBe("error");
  });

  it("still says saving while a save is in flight", () => {
    expect(resolveDrawerSaveState("saving", true)).toBe("saving");
  });

  it("says dirty when there is nothing else to report", () => {
    expect(resolveDrawerSaveState(undefined, true)).toBe("dirty");
  });

  it("says saved once the form matches what was stored", () => {
    expect(resolveDrawerSaveState("saved", false)).toBe("saved");
  });

  it("is clean with no state and no edits", () => {
    expect(resolveDrawerSaveState(undefined, false)).toBe("clean");
  });
});

describe("shouldClearSaveState", () => {
  it("drops a stale saved badge once the owner edits again", () => {
    expect(shouldClearSaveState("saved", true)).toBe(true);
  });

  it("keeps a refusal on screen while the form is dirty", () => {
    // This is the whole defect. Clearing here is what discarded the only
    // explanation the owner had for why pressing the button did nothing.
    expect(shouldClearSaveState("error", true)).toBe(false);
  });

  it("leaves a settled form alone", () => {
    expect(shouldClearSaveState("saved", false)).toBe(false);
    expect(shouldClearSaveState(undefined, true)).toBe(false);
  });
});

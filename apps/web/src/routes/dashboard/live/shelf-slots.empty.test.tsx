import { renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { closeSlot, openSlot, settleSlot, useOpenSlots } from "./shelf-slots";

describe("counting the posters on their way, before the row has been drawn", () => {
  it("counts none when the row is not there yet", () => {
    const { result } = renderHook(() => useOpenSlots({ current: null }));
    expect(result.current).toBe(0);
  });
});

describe("a poster that is not standing in a slot", () => {
  it("is left alone whether the slot is opened, closed or settled", () => {
    const art = document.createElement("span");
    art.getBoundingClientRect = () => ({ width: 80, height: 120, top: 0, left: 0, right: 80, bottom: 120, x: 0, y: 0, toJSON: () => ({}) });

    expect(() => { openSlot(art, 0.5); closeSlot(art); settleSlot(art); }).not.toThrow();
    expect(art.getAttribute("style")).toBeNull();
  });
});

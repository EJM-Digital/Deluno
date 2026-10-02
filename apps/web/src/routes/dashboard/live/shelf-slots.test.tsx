import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { FLYING_ATTRIBUTE, HELD_ATTRIBUTE, SHELF_GAP_PX, announceSlotsChanged, closeSlot, openSlot, settleSlot, useOpenSlots } from "./shelf-slots";

function row(): { list: HTMLUListElement; art: HTMLElement; slot: HTMLLIElement } {
  const list = document.createElement("ul");
  const slot = document.createElement("li");
  const art = document.createElement("span");
  art.getBoundingClientRect = () => ({ width: 80, height: 120, top: 0, left: 0, right: 80, bottom: 120, x: 0, y: 0, toJSON: () => ({}) });
  slot.appendChild(art);
  list.appendChild(slot);
  document.body.appendChild(list);
  return { list, art, slot };
}

describe("the shelf's slot for a poster on its way", () => {
  afterEach(() => {
    document.body.innerHTML = "";
  });

  it("is closed to no width with its gap taken back, opens with the flight and gives its own width back when settled", () => {
    const { art, slot } = row();
    closeSlot(art);
    expect(slot.style.width).toBe("0px");
    expect(slot.style.marginRight).toBe(`${-SHELF_GAP_PX}px`);
    expect(slot.style.overflow).toBe("hidden");
    openSlot(art, 0.5);
    expect(slot.style.width).toBe("40px");
    expect(Number.parseFloat(slot.style.marginRight)).toBeCloseTo(-SHELF_GAP_PX / 2);
    settleSlot(art);
    expect(slot.style.width).toBe("");
    expect(slot.style.marginRight).toBe("");
    expect(slot.style.overflow).toBe("");
  });

  it("counts the posters held or flying, so the row keeps one more for each", async () => {
    const { list, art } = row();
    const { result } = renderHook(() => useOpenSlots({ current: list }));
    expect(result.current).toBe(0);
    art.setAttribute(HELD_ATTRIBUTE, "");
    announceSlotsChanged();
    await waitFor(() => expect(result.current).toBe(1));
    art.removeAttribute(HELD_ATTRIBUTE);
    art.setAttribute(FLYING_ATTRIBUTE, "");
    announceSlotsChanged();
    await waitFor(() => expect(result.current).toBe(1));
    art.removeAttribute(FLYING_ATTRIBUTE);
    announceSlotsChanged();
    await waitFor(() => expect(result.current).toBe(0));
  });
});

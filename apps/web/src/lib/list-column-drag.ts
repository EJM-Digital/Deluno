import { flushSync } from "react-dom";

/**
 * Pointer-driven column dragging for ListTable (#1119).
 *
 * Nothing here re-renders React while the pointer moves. The dragged header
 * (and its cells) follow the pointer with a transform only, written once per
 * animation frame; the columns it passes slide aside with short transform
 * transitions; and the new order is committed to React exactly once, on drop,
 * after the dragged column has settled into its slot. Escape (or a lost
 * pointer) slides everything back.
 */

export const COLUMN_EASE = "cubic-bezier(0.2, 0, 0, 1)";
export const COLUMN_MOVE_MS = 180;
const MOUSE_THRESHOLD = 5;
const TOUCH_HOLD_MS = 200;
const TOUCH_SLOP = 8;
const EDGE_SCROLL_ZONE = 48;

export function prefersReducedMotion(): boolean {
  try {
    return typeof window !== "undefined" && window.matchMedia?.("(prefers-reduced-motion: reduce)").matches === true;
  } catch {
    return false;
  }
}

export interface ColumnDragDeps {
  headerRow: HTMLElement;
  body: HTMLElement;
  /** Column ids in the order they are displayed now. */
  order: string[];
  isMovable: (id: string) => boolean;
  /** Called once on drop with the new display order. */
  commit: (order: string[]) => void;
}

interface Slot {
  id: string;
  header: HTMLElement;
  cells: HTMLElement[];
  left: number;
  width: number;
}

const TOUCHED_PROPS = ["transform", "transition", "zIndex", "position", "boxShadow", "backgroundColor", "willChange", "cursor"] as const;

function clearStyles(el: HTMLElement) {
  for (const prop of TOUCHED_PROPS) el.style[prop] = "";
}

function sameOrder(a: string[], b: string[]) {
  return a.length === b.length && a.every((id, index) => id === b[index]);
}

/** Slot ids in display order, with `id` moved to the place its centre is over. Pure, so it can be tested without a browser. */
export function dropOrder(order: string[], movableIds: string[], visibleIds: string[], midpoints: Record<string, number>, id: string, centre: number): string[] {
  const movable = new Set(movableIds);
  const visibleMovable = order.filter((candidate) => movable.has(candidate) && visibleIds.includes(candidate));
  const others = visibleMovable.filter((candidate) => candidate !== id);
  const index = others.filter((candidate) => midpoints[candidate] < centre).length;
  const nextVisible = [...others.slice(0, index), id, ...others.slice(index)];
  let taken = 0;
  const movableSlots = order.filter((candidate) => movable.has(candidate));
  const nextMovable = movableSlots.map((candidate) => (visibleIds.includes(candidate) ? nextVisible[taken++] : candidate));
  let next = 0;
  return order.map((candidate) => (movable.has(candidate) ? nextMovable[next++] : candidate));
}

let busy = false;

export function trackColumnDrag(event: { button: number; pointerType: string; clientX: number; clientY: number }, id: string, deps: ColumnDragDeps): void {
  if (event.pointerType === "mouse" && event.button !== 0) return;
  if (!deps.isMovable(id) || busy) return;
  busy = true;

  const touch = event.pointerType === "touch";
  const startX = event.clientX;
  const startY = event.clientY;
  const reduced = prefersReducedMotion();
  const duration = reduced ? 0 : COLUMN_MOVE_MS;

  let started = false;
  let finished = false;
  let latestX = startX;
  let frame = 0;
  let holdTimer: ReturnType<typeof setTimeout> | undefined;
  let slots: Slot[] = [];
  let dragged: Slot | undefined;
  let scroller: HTMLElement | null = null;
  let scrollStart = 0;
  let gap = 0;
  let firstLeft = 0;
  let rowLeft = 0;
  let rowRight = 0;
  let midpoints: Record<string, number> = {};
  let widths: Record<string, number> = {};
  let lefts: Record<string, number> = {};
  let target = deps.order;
  let scrollDirection = 0;
  const savedBody = { userSelect: "", cursor: "" };

  const transition = `transform ${duration}ms ${COLUMN_EASE}, box-shadow ${duration}ms ${COLUMN_EASE}`;

  function findScroller(): HTMLElement | null {
    let node: HTMLElement | null = deps.headerRow.parentElement;
    while (node && node !== document.body) {
      const overflowX = getComputedStyle(node).overflowX;
      if ((overflowX === "auto" || overflowX === "scroll") && node.scrollWidth > node.clientWidth) return node;
      node = node.parentElement;
    }
    return null;
  }

  function measure() {
    const headers = Array.from(deps.headerRow.querySelectorAll<HTMLElement>('[role="columnheader"][data-col-id]'));
    // Group headings are role=row too but hold no cells, so they are left where they are.
    const rows = Array.from(deps.body.querySelectorAll<HTMLElement>('[role="row"]')).filter((row) => row.querySelector(":scope > [role=cell]"));
    slots = headers
      .map((header) => {
        const rect = header.getBoundingClientRect();
        const columnId = header.dataset.colId as string;
        const position = deps.order.indexOf(columnId);
        const cells = rows.map((row) => row.children[position] as HTMLElement | undefined).filter((cell): cell is HTMLElement => Boolean(cell));
        return { id: columnId, header, cells, left: rect.left, width: rect.width };
      })
      .filter((slot) => slot.width > 0)
      .sort((a, b) => a.left - b.left);
    dragged = slots.find((slot) => slot.id === id);
    midpoints = Object.fromEntries(slots.map((slot) => [slot.id, slot.left + slot.width / 2]));
    widths = Object.fromEntries(slots.map((slot) => [slot.id, slot.width]));
    lefts = Object.fromEntries(slots.map((slot) => [slot.id, slot.left]));
    gap = slots.length > 1 ? Math.max(0, slots[1].left - (slots[0].left + slots[0].width)) : 0;
    firstLeft = slots[0]?.left ?? 0;
    const rowRect = deps.headerRow.getBoundingClientRect();
    rowLeft = rowRect.left;
    rowRight = rowRect.right;
    scroller = findScroller();
    scrollStart = scroller?.scrollLeft ?? 0;
  }

  function shift(slot: Slot, x: number, extra?: { lift?: boolean }) {
    const lift = extra?.lift ?? false;
    slot.header.style.transform = lift ? `translate3d(${x}px,0,0) scale(1.02)` : `translate3d(${x}px,0,0)`;
    for (const cell of slot.cells) cell.style.transform = `translate3d(${x}px,0,0)`;
  }

  function setTransition(slot: Slot, value: string) {
    slot.header.style.transition = value;
    for (const cell of slot.cells) cell.style.transition = value;
  }

  function layoutLefts(order: string[]) {
    const result: Record<string, number> = {};
    let x = firstLeft;
    for (const columnId of order) {
      if (!(columnId in widths)) continue;
      result[columnId] = x;
      x += widths[columnId] + gap;
    }
    return result;
  }

  function begin() {
    started = true;
    measure();
    if (!dragged) {
      finish(false);
      return;
    }
    savedBody.userSelect = document.body.style.userSelect;
    savedBody.cursor = document.body.style.cursor;
    document.body.style.userSelect = "none";
    document.body.style.cursor = "grabbing";
    for (const slot of slots) {
      slot.header.style.willChange = "transform";
      if (slot.id !== id) setTransition(slot, transition);
    }
    dragged.header.style.transition = "box-shadow 120ms ease-out";
    dragged.header.style.zIndex = "5";
    dragged.header.style.position = "relative";
    dragged.header.style.cursor = "grabbing";
    dragged.header.style.boxShadow = "0 8px 22px -6px hsl(0 0% 0% / 0.45), 0 0 0 1px hsl(var(--primary) / 0.45)";
    dragged.header.style.backgroundColor = "color-mix(in srgb, hsl(var(--primary)) 14%, hsl(var(--card)))";
    for (const cell of dragged.cells) {
      cell.style.position = "relative";
      cell.style.zIndex = "4";
      cell.style.backgroundColor = "color-mix(in srgb, hsl(var(--primary)) 8%, hsl(var(--card)))";
    }
    schedule();
  }

  function schedule() {
    if (!frame) frame = requestAnimationFrame(update);
  }

  function update() {
    frame = 0;
    if (!started || finished || !dragged) return;
    if (scroller && scrollDirection !== 0) {
      scroller.scrollLeft += scrollDirection * 14;
      schedule();
    }
    const scrolled = scroller ? scroller.scrollLeft - scrollStart : 0;
    const raw = latestX - startX + scrolled;
    const dx = Math.min(Math.max(raw, rowLeft - dragged.left), rowRight - (dragged.left + dragged.width));
    shift(dragged, dx, { lift: true });

    const centre = dragged.left + dragged.width / 2 + dx;
    const visibleIds = slots.map((slot) => slot.id);
    const next = dropOrder(deps.order, deps.order.filter(deps.isMovable), visibleIds, midpoints, id, centre);
    if (sameOrder(next, target)) return;
    target = next;
    const newLefts = layoutLefts(next);
    for (const slot of slots) {
      if (slot.id === id) continue;
      shift(slot, newLefts[slot.id] - lefts[slot.id]);
    }
  }

  function onMove(moveEvent: PointerEvent) {
    latestX = moveEvent.clientX;
    if (!started) {
      const distance = Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY);
      if (touch) {
        if (distance > TOUCH_SLOP) finish(false);
        return;
      }
      if (distance >= MOUSE_THRESHOLD) begin();
      return;
    }
    moveEvent.preventDefault();
    if (scroller) {
      const rect = scroller.getBoundingClientRect();
      scrollDirection = moveEvent.clientX > rect.right - EDGE_SCROLL_ZONE ? 1 : moveEvent.clientX < rect.left + EDGE_SCROLL_ZONE ? -1 : 0;
    }
    schedule();
  }

  function swallowNextClick() {
    const stop = (clickEvent: Event) => {
      clickEvent.stopPropagation();
      clickEvent.preventDefault();
    };
    window.addEventListener("click", stop, { capture: true, once: true });
    setTimeout(() => window.removeEventListener("click", stop, { capture: true }), 0);
  }

  function release() {
    window.removeEventListener("pointermove", onMove);
    window.removeEventListener("pointerup", onUp);
    window.removeEventListener("pointercancel", onCancel);
    window.removeEventListener("keydown", onKey, true);
    if (holdTimer) clearTimeout(holdTimer);
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    scrollDirection = 0;
  }

  function resetEverything() {
    for (const slot of slots) {
      clearStyles(slot.header);
      for (const cell of slot.cells) clearStyles(cell);
    }
    if (started) {
      document.body.style.userSelect = savedBody.userSelect;
      document.body.style.cursor = savedBody.cursor;
    }
    busy = false;
  }

  /** Settle the dragged column into `order`'s slot (or back home) and then hand the order to React. */
  function finish(commit: boolean) {
    if (finished) return;
    finished = true;
    release();
    if (!started || !dragged) {
      resetEverything();
      return;
    }
    swallowNextClick();
    const finalOrder = commit ? target : deps.order;
    const finalLefts = layoutLefts(finalOrder);
    for (const slot of slots) {
      setTransition(slot, transition);
      const x = (finalLefts[slot.id] ?? lefts[slot.id]) - lefts[slot.id];
      shift(slot, commit ? x : 0);
    }
    dragged.header.style.boxShadow = "none";
    const done = () => {
      if (commit && !sameOrder(finalOrder, deps.order)) {
        // Commit and clear in the same frame: the cells land where the transforms left them, so nothing jumps.
        flushSync(() => deps.commit(finalOrder));
      }
      resetEverything();
    };
    if (duration === 0) done();
    else setTimeout(done, duration + 20);
  }

  function onUp() {
    finish(true);
  }

  function onCancel() {
    finish(false);
  }

  function onKey(keyEvent: KeyboardEvent) {
    if (keyEvent.key === "Escape" && started) {
      keyEvent.preventDefault();
      keyEvent.stopPropagation();
      finish(false);
    }
  }

  window.addEventListener("pointermove", onMove, { passive: false });
  window.addEventListener("pointerup", onUp);
  window.addEventListener("pointercancel", onCancel);
  window.addEventListener("keydown", onKey, true);
  if (touch) holdTimer = setTimeout(begin, TOUCH_HOLD_MS);
}

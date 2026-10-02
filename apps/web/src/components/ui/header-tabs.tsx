import * as React from "react";
import { createPortal } from "react-dom";
import { ChevronDown } from "lucide-react";
import { NavLink, useLocation } from "react-router-dom";
import { cn } from "../../lib/utils";
import { Button } from "./button";

/** The gap between two tabs, in px. Matches `gap-[18px]` below. */
export const HEADER_TAB_GAP = 18;

export interface HeaderTabItem {
  key: string;
  label: string;
  selected: boolean;
  /** A link to another route. Without it the tab is a button that calls `onSelect`. */
  to?: string;
  end?: boolean;
  onSelect?: () => void;
  /** Set for a tab that is a button (role="tab"), e.g. the Dashboard's. */
  buttonId?: string;
  controls?: string;
  ariaLabel?: string;
  status?: "complete" | "pending";
}

export interface FitResult {
  /** Indexes shown in the row, in the order they are drawn. */
  visible: number[];
  /** Indexes folded into the More menu, in their own order. */
  folded: number[];
}

/**
 * Which tabs show when `room` px are available, and which fold into "More".
 *
 * Everything shows when it all fits. Otherwise the More button takes its own
 * width, the tabs that fit in what is left show from the front, and the rest
 * fold. The chosen tab is never folded: when it would be, it takes the last
 * visible place and the tab it displaced folds instead.
 */
export function fitHeaderTabs(widths: readonly number[], room: number, moreWidth: number, selectedIndex: number, gap = HEADER_TAB_GAP): FitResult {
  const count = widths.length;
  const all = widths.reduce((sum, width) => sum + width, 0) + gap * Math.max(0, count - 1);
  if (all <= room) return { visible: widths.map((_, index) => index), folded: [] };

  const available = room - moreWidth - gap;
  // The most tabs from the front that fit beside More.
  let prefix = 0;
  let used = 0;
  while (prefix < count) {
    const next = used + widths[prefix]! + (prefix > 0 ? gap : 0);
    if (next > available) break;
    used = next;
    prefix += 1;
  }

  const hasSelected = selectedIndex >= 0 && selectedIndex < count;
  if (!hasSelected || selectedIndex < prefix) {
    return {
      visible: Array.from({ length: prefix }, (_, index) => index),
      folded: Array.from({ length: count - prefix }, (_, index) => prefix + index)
    };
  }

  // The chosen tab is past the cut: it takes the last visible place.
  let keep = 0;
  used = widths[selectedIndex]!;
  while (keep < selectedIndex) {
    const next = used + gap + widths[keep]!;
    if (next > available) break;
    used = next;
    keep += 1;
  }
  const visible = [...Array.from({ length: keep }, (_, index) => index), selectedIndex];
  const folded = widths.map((_, index) => index).filter((index) => !visible.includes(index));
  return { visible, folded };
}

const TAB_CLASS =
  "relative whitespace-nowrap rounded-sm text-[length:var(--type-body-sm)] font-medium leading-none after:absolute after:inset-x-0 after:-bottom-[7px] after:h-[2px] after:rounded-full after:content-[''] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring";

function tabTone(selected: boolean) {
  return selected ? "text-primary after:bg-primary hover:text-primary" : "text-muted-foreground after:bg-transparent hover:text-foreground";
}

const LG_QUERY = "(min-width: 1024px)";

function subscribeToWidth(onChange: () => void) {
  if (typeof window.matchMedia !== "function") return () => undefined;
  const list = window.matchMedia(LG_QUERY);
  list.addEventListener("change", onChange);
  return () => list.removeEventListener("change", onChange);
}

/**
 * The header's slot for tabs, or null where tabs do not go in it: below `lg` (the slot is hidden there, so the page
 * draws its own row) and on a page rendered without the header. Exactly one place draws the tabs at a time, so the
 * page never holds a hidden second copy.
 */
export function useHeaderSlot(): HTMLElement | null {
  const wide = React.useSyncExternalStore(
    subscribeToWidth,
    () => typeof window.matchMedia === "function" && window.matchMedia(LG_QUERY).matches,
    () => false
  );
  const [slot, setSlot] = React.useState<HTMLElement | null>(null);
  React.useLayoutEffect(() => {
    setSlot(document.getElementById("topbar-page-tabs"));
  }, []);
  return wide ? slot : null;
}

/** Puts `children` in the header's tab slot (shown at `lg` and up), or nothing when the page has no header. */
export function HeaderTabsPortal({ slot, children }: { slot: HTMLElement | null; children: React.ReactNode }) {
  return slot ? createPortal(children, slot) : null;
}

/**
 * The one tab control in Deluno (the owner, 2 Oct 2026): plain 14px text 18px apart on the header's title line, the
 * chosen one in the accent colour with a 2px underline. It takes the room it is given; tabs that do not fit fold
 * into a "More" menu. Widths are measured in a hidden copy before paint and again on resize and when fonts load.
 */
export function HeaderTabs({ items, label, role }: {
  items: readonly HeaderTabItem[];
  label: string;
  /** "tablist" for tabs that are buttons; omit for a nav of links. */
  role?: "tablist";
}) {
  const rootRef = React.useRef<HTMLDivElement>(null);
  const measureRef = React.useRef<HTMLDivElement>(null);
  const moreRef = React.useRef<HTMLButtonElement>(null);
  const menuRef = React.useRef<HTMLDivElement>(null);
  const menuId = React.useId();
  const location = useLocation();
  const [measure, setMeasure] = React.useState<{ widths: number[]; more: number; room: number } | null>(null);
  const [open, setOpen] = React.useState(false);
  const [anchor, setAnchor] = React.useState<{ top: number; left: number } | null>(null);

  const signature = items.map((item) => item.label).join("|");

  const measureNow = React.useCallback(() => {
    const root = rootRef.current;
    const layer = measureRef.current;
    if (!root || !layer) return;
    const children = Array.from(layer.children) as HTMLElement[];
    const widths = children.slice(0, -1).map((child) => Math.ceil(child.getBoundingClientRect().width));
    const more = Math.ceil(children[children.length - 1]?.getBoundingClientRect().width ?? 0);
    const room = Math.floor(root.getBoundingClientRect().width);
    setMeasure((previous) =>
      previous && previous.room === room && previous.more === more && previous.widths.length === widths.length && previous.widths.every((width, index) => width === widths[index])
        ? previous
        : { widths, more, room }
    );
  }, []);

  React.useLayoutEffect(() => {
    measureNow();
  }, [measureNow, signature]);

  React.useEffect(() => {
    const root = rootRef.current;
    if (!root) return;
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(() => measureNow());
    observer?.observe(root);
    const fonts = typeof document !== "undefined" ? document.fonts : undefined;
    void fonts?.ready.then(measureNow);
    fonts?.addEventListener?.("loadingdone", measureNow);
    return () => {
      observer?.disconnect();
      fonts?.removeEventListener?.("loadingdone", measureNow);
    };
  }, [measureNow]);

  const selectedIndex = items.findIndex((item) => item.selected);
  // No room measured (hidden below `lg`, or no layout at all) means nothing is folded: there is nothing to fit into.
  const fit = measure && measure.room > 0 && measure.widths.length === items.length
    ? fitHeaderTabs(measure.widths, measure.room, measure.more, selectedIndex)
    : { visible: items.map((_, index) => index), folded: [] as number[] };
  const folded = fit.folded;

  // The menu closes whenever the route changes or nothing is folded any more.
  React.useEffect(() => { setOpen(false); }, [location.pathname]);
  React.useEffect(() => { if (folded.length === 0) setOpen(false); }, [folded.length]);

  React.useLayoutEffect(() => {
    if (!open) return;
    function place() {
      const rect = moreRef.current?.getBoundingClientRect();
      if (rect) setAnchor({ top: rect.bottom + 8, left: rect.left });
    }
    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open]);

  React.useEffect(() => {
    if (!open) return;
    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      if (moreRef.current?.contains(target) || menuRef.current?.contains(target)) return;
      setOpen(false);
    }
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [open]);

  React.useEffect(() => {
    if (open && anchor) menuRef.current?.querySelector<HTMLElement>("[role=menuitem]")?.focus();
  }, [open, anchor]);

  function closeAndReturn() {
    setOpen(false);
    moreRef.current?.focus();
  }

  function onMenuKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    const entries = Array.from(menuRef.current?.querySelectorAll<HTMLElement>("[role=menuitem]") ?? []);
    const index = entries.indexOf(document.activeElement as HTMLElement);
    if (event.key === "ArrowDown") { event.preventDefault(); entries[(index + 1) % entries.length]?.focus(); }
    else if (event.key === "ArrowUp") { event.preventDefault(); entries[(index - 1 + entries.length) % entries.length]?.focus(); }
    else if (event.key === "Home") { event.preventDefault(); entries[0]?.focus(); }
    else if (event.key === "End") { event.preventDefault(); entries[entries.length - 1]?.focus(); }
    else if (event.key === "Escape") { event.preventDefault(); closeAndReturn(); }
    else if (event.key === "Tab") setOpen(false);
  }

  const moreButton = (
    <Button
      ref={moreRef}
      type="button"
      variant="ghost"
      size="sm"
      aria-haspopup="menu"
      aria-expanded={open}
      aria-controls={open ? menuId : undefined}
      onClick={() => setOpen((value) => !value)}
      onKeyDown={(event) => { if (event.key === "ArrowDown" && !open) { event.preventDefault(); setOpen(true); } }}
      // Never drawn as chosen: only the chosen tab is underlined.
      className="relative h-auto gap-1 rounded-sm bg-transparent p-0 text-[length:var(--type-body-sm)] font-medium leading-none text-muted-foreground hover:bg-transparent hover:text-foreground"
    >
      More
      <ChevronDown aria-hidden="true" className={cn("h-3.5 w-3.5 shrink-0 transition-transform", open && "rotate-180")} />
    </Button>
  );

  function renderTab(item: HeaderTabItem) {
    if (item.to) {
      return (
        <NavLink
          key={item.key}
          to={item.to}
          end={item.end}
          aria-label={item.ariaLabel}
          data-status={item.status}
          className={cn(TAB_CLASS, "inline-flex items-center transition-colors duration-200", tabTone(item.selected))}
        >
          {item.label}
        </NavLink>
      );
    }
    return (
      <Button
        key={item.key}
        type="button"
        role="tab"
        id={item.buttonId}
        aria-selected={item.selected}
        aria-controls={item.controls}
        variant="ghost"
        size="sm"
        onClick={item.onSelect}
        className={cn("h-auto bg-transparent p-0 hover:bg-transparent", TAB_CLASS, tabTone(item.selected))}
      >
        {item.label}
      </Button>
    );
  }

  const row = (
    <div role={role} aria-label={role ? label : undefined} className="flex min-w-0 items-baseline gap-[18px]">
      {fit.visible.map((index) => renderTab(items[index]!))}
      {folded.length > 0 ? moreButton : null}
    </div>
  );

  return (
    <div ref={rootRef} className="relative w-full min-w-0">
      {role ? row : <nav aria-label={label}>{row}</nav>}
      {/* A hidden copy of every tab and of More, only to measure. Never read, never focusable. */}
      <div ref={measureRef} aria-hidden="true" className="pointer-events-none invisible absolute left-0 top-0 flex items-baseline gap-[18px] whitespace-nowrap" style={{ height: 0, overflow: "hidden" }}>
        {items.map((item) => (
          // The text is drawn from an attribute so the page holds no second copy of a tab's words.
          <span key={item.key} data-label={item.label} className={cn(TAB_CLASS, "inline-flex before:content-[attr(data-label)]")} />
        ))}
        <span data-label="More" className="inline-flex items-center gap-1 text-[length:var(--type-body-sm)] font-medium leading-none before:content-[attr(data-label)]">
          <ChevronDown aria-hidden="true" className="h-3.5 w-3.5 shrink-0" />
        </span>
      </div>
      {open && anchor
        ? createPortal(
            <div
              ref={menuRef}
              id={menuId}
              role="menu"
              aria-label={`More ${label.toLowerCase()}`}
              onKeyDown={onMenuKeyDown}
              style={{ top: anchor.top, left: anchor.left }}
              className="fixed z-50 min-w-[11rem] rounded-xl border border-hairline bg-card p-1 shadow-[var(--shadow-card)]"
            >
              {folded.map((index) => {
                const item = items[index]!;
                const classes = "flex w-full items-center rounded-lg px-3 py-2 text-left text-[length:var(--type-body-sm)] font-medium text-muted-foreground outline-none hover:bg-secondary hover:text-foreground focus-visible:bg-secondary focus-visible:text-foreground";
                return item.to ? (
                  <NavLink key={item.key} to={item.to} end={item.end} role="menuitem" aria-label={item.ariaLabel} onClick={() => setOpen(false)} className={classes}>
                    {item.label}
                  </NavLink>
                ) : (
                  <button key={item.key} type="button" role="menuitem" onClick={() => { setOpen(false); item.onSelect?.(); }} className={classes}>
                    {item.label}
                  </button>
                );
              })}
            </div>,
            document.body
          )
        : null}
    </div>
  );
}

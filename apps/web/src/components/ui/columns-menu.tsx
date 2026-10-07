import * as React from "react";
import { createPortal } from "react-dom";
import { cn } from "../../lib/utils";

const MOVE_HINT = "Drag a heading to move its column, or press Alt with the left or right arrow on it.";
const SORT_HINT = " Click a heading to sort by it, and click again to reverse.";

/** What a table says about itself to the card that holds its Columns button. */
export interface ColumnsControls {
  /** The id of the hint every movable heading points at. */
  hintId: string;
  /** Whether the headings sort the table. */
  sortable: boolean;
  /** What a screen reader is told about the last move. */
  announcement: string;
  /** Puts the columns and the sort back as they started. */
  reset: () => void;
}

/**
 * The small Columns button on a list's card header, the same as Weir's, with the one thing it offers: putting the
 * columns and sort back as they started. Portalled, because the card clips what overflows it.
 */
export function ColumnsButton({ onReset, className }: { onReset: () => void; className?: string }) {
  const menuId = React.useId();
  const [open, setOpen] = React.useState(false);
  const [anchor, setAnchor] = React.useState<{ top: number; right: number } | null>(null);
  const rootRef = React.useRef<HTMLDivElement>(null);
  const triggerRef = React.useRef<HTMLButtonElement>(null);
  const menuRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    if (!open) return;
    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      if (rootRef.current?.contains(target) || menuRef.current?.contains(target)) return;
      setOpen(false);
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        event.preventDefault();
        setOpen(false);
        triggerRef.current?.focus();
      }
    }
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  React.useLayoutEffect(() => {
    if (!open) return;
    function measure() {
      const rect = triggerRef.current?.getBoundingClientRect();
      if (rect) setAnchor({ top: rect.bottom + 6, right: window.innerWidth - rect.right });
    }
    measure();
    window.addEventListener("resize", measure);
    window.addEventListener("scroll", measure, true);
    return () => {
      window.removeEventListener("resize", measure);
      window.removeEventListener("scroll", measure, true);
    };
  }, [open]);

  return (
    // From md up only: on a phone the lists collapse to their first columns and there is nothing to rearrange, and the
    // button pushed a card header past the screen's edge (settings-routes-fit-a-phone, /system at 390px).
    <div ref={rootRef} className={cn("relative hidden shrink-0 md:block", className)}>
      <button
        type="button"
        ref={triggerRef}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => setOpen((current) => !current)}
        className={cn(
          "h-[calc(28px*var(--ui-scale))] rounded-[8px] border border-hairline bg-transparent px-[10px] text-[length:calc(13px*var(--font-scale))] font-medium text-muted-foreground transition-colors",
          "hover:bg-surface-2 hover:text-foreground focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary",
          open && "bg-surface-2 text-foreground"
        )}
      >
        Columns
      </button>
      {open && anchor
        ? createPortal(
            <div
              ref={menuRef}
              id={menuId}
              role="menu"
              aria-label="Columns"
              style={{ position: "fixed", top: anchor.top, right: anchor.right }}
              className="z-80 min-w-[160px] overflow-hidden rounded-xl border border-hairline/80 bg-popover p-1.5 shadow-lg dark:border-white/[0.07]"
            >
              <button
                type="button"
                role="menuitem"
                autoFocus
                onClick={() => {
                  setOpen(false);
                  onReset();
                  triggerRef.current?.focus();
                }}
                className="flex min-h-(--control-height-sm) w-full items-center rounded-lg px-3 text-left text-(length:--shell-nav-size) font-medium text-foreground transition hover:bg-muted/45 focus-visible:bg-muted/45 focus-visible:outline-hidden"
              >
                Reset columns
              </button>
            </div>,
            document.body
          )
        : null}
    </div>
  );
}

const sharedHints = new Map<string, number>();

/**
 * The one line a screen reader is told about moving and sorting columns,
 * which every movable heading points at (#1162).
 *
 * <p>It used to be printed once per table, so a page of three tables said it
 * three times. It now lives once per page, as a single hidden element that the
 * first table creates and the last one to go removes.</p>
 *
 * @returns the id of that element, for aria-describedby.
 */
export function useSharedColumnsHint(sortable: boolean): string {
  const id = sortable ? "deluno-columns-hint-sort" : "deluno-columns-hint";
  React.useEffect(() => {
    const count = sharedHints.get(id) ?? 0;
    sharedHints.set(id, count + 1);
    if (count === 0 && !document.getElementById(id)) {
      const element = document.createElement("p");
      element.id = id;
      element.className = "sr-only";
      element.textContent = MOVE_HINT + (sortable ? SORT_HINT : "");
      document.body.appendChild(element);
    }
    return () => {
      const remaining = (sharedHints.get(id) ?? 1) - 1;
      if (remaining > 0) {
        sharedHints.set(id, remaining);
        return;
      }
      sharedHints.delete(id);
      document.getElementById(id)?.remove();
    };
  }, [id, sortable]);
  return id;
}

/** What a screen reader is told after each column move. */
export function ColumnsAnnouncements({ controls }: { controls: readonly ColumnsControls[] }) {
  return (
    <>
      <p role="status" aria-live="polite" className="sr-only">
        {controls
          .map((entry) => entry.announcement)
          .filter(Boolean)
          .join(" ")}
      </p>
    </>
  );
}

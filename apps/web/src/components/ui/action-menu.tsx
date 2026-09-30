import * as React from "react";
import { createPortal } from "react-dom";
import { Ellipsis } from "lucide-react";
import { cn } from "../../lib/utils";
import { Button } from "./button";

export interface ActionMenuItem {
  label: string;
  onSelect: () => void;
  icon?: React.ReactNode;
  /** Red text, for the one item that removes something. */
  destructive?: boolean;
  disabled?: boolean;
}

/**
 * One "More" button holding the rarely used commands, so a panel can keep its
 * common buttons visible without growing (#1058).
 *
 * A menu, not a listbox: each item runs a command. Arrow keys, Home, End and
 * Escape work, Escape returns focus to the button, and the list is portalled to
 * the page so no clipping parent can cut it off (the same reasons as MenuSelect).
 */
export function ActionMenu({
  label = "More",
  ariaLabel = "More actions",
  items,
  className,
  triggerClassName
}: {
  label?: string;
  ariaLabel?: string;
  items: ActionMenuItem[];
  className?: string;
  triggerClassName?: string;
}) {
  const listId = React.useId();
  const [open, setOpen] = React.useState(false);
  const [anchor, setAnchor] = React.useState<{ top: number; right: number } | null>(null);
  const rootRef = React.useRef<HTMLDivElement>(null);
  const triggerRef = React.useRef<HTMLButtonElement>(null);
  const menuRef = React.useRef<HTMLDivElement>(null);
  const itemRefs = React.useRef<Array<HTMLButtonElement | null>>([]);

  React.useEffect(() => {
    if (!open) return;
    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      if (rootRef.current?.contains(target) || menuRef.current?.contains(target)) return;
      setOpen(false);
    }
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [open]);

  React.useLayoutEffect(() => {
    if (!open) return;
    function measure() {
      const rect = triggerRef.current?.getBoundingClientRect();
      if (!rect) return;
      setAnchor({ top: rect.bottom + 6, right: window.innerWidth - rect.right });
    }
    measure();
    window.addEventListener("resize", measure);
    window.addEventListener("scroll", measure, true);
    return () => {
      window.removeEventListener("resize", measure);
      window.removeEventListener("scroll", measure, true);
    };
  }, [open]);

  React.useEffect(() => {
    if (open && anchor) itemRefs.current[0]?.focus();
  }, [open, anchor]);

  function close(returnFocus: boolean) {
    setOpen(false);
    if (returnFocus) triggerRef.current?.focus();
  }

  function moveFocus(from: number, delta: number) {
    const enabled = items.map((item, index) => (item.disabled ? -1 : index)).filter((index) => index >= 0);
    if (enabled.length === 0) return;
    const position = enabled.indexOf(from);
    const next = enabled[(position + delta + enabled.length) % enabled.length];
    itemRefs.current[next]?.focus();
  }

  function onItemKeyDown(event: React.KeyboardEvent, index: number) {
    switch (event.key) {
      case "ArrowDown": event.preventDefault(); moveFocus(index, 1); break;
      case "ArrowUp": event.preventDefault(); moveFocus(index, -1); break;
      case "Home": event.preventDefault(); itemRefs.current[0]?.focus(); break;
      case "End": event.preventDefault(); itemRefs.current[items.length - 1]?.focus(); break;
      case "Escape": event.preventDefault(); close(true); break;
      case "Tab": setOpen(false); break;
      default: break;
    }
  }

  return (
    <div ref={rootRef} className={cn("relative", className)}>
      <Button
        ref={triggerRef}
        type="button"
        variant="outline"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-label={ariaLabel}
        onClick={() => setOpen((current) => !current)}
        onKeyDown={(event) => {
          if (event.key === "ArrowDown") {
            event.preventDefault();
            setOpen(true);
          }
        }}
        className={cn("w-full", triggerClassName)}
      >
        <Ellipsis aria-hidden className="h-4 w-4" />
        {label}
      </Button>
      {open && anchor ? createPortal(
        <div
          ref={menuRef}
          id={listId}
          role="menu"
          aria-label={ariaLabel}
          style={{ position: "fixed", top: anchor.top, right: anchor.right }}
          className="z-[80] min-w-[220px] overflow-hidden rounded-xl border border-hairline/80 bg-popover p-1.5 shadow-lg dark:border-white/[0.07]"
        >
          {items.map((item, index) => (
            <button
              key={item.label}
              ref={(node) => { itemRefs.current[index] = node; }}
              type="button"
              role="menuitem"
              disabled={item.disabled}
              onKeyDown={(event) => onItemKeyDown(event, index)}
              onClick={() => {
                close(false);
                item.onSelect();
              }}
              className={cn(
                "flex min-h-[var(--control-height-sm)] w-full items-center gap-2.5 rounded-lg px-3 text-left",
                "text-[length:var(--shell-nav-size)] font-medium transition hover:bg-muted/45 focus-visible:bg-muted/45 disabled:opacity-50",
                item.destructive ? "text-destructive hover:bg-destructive/10" : "text-foreground"
              )}
            >
              {item.icon ? <span aria-hidden className="shrink-0">{item.icon}</span> : null}
              {item.label}
            </button>
          ))}
        </div>,
        document.body
      ) : null}
    </div>
  );
}

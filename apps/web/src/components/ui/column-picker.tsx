import * as React from "react";
import { createPortal } from "react-dom";
import { Check, Columns3 } from "lucide-react";
import { cn } from "../../lib/utils";
import type { ColumnDefinition } from "../../lib/use-column-visibility";

/**
 * "Options" (#798) / column picker (#796): which optional columns a table
 * shows, chosen from a small checklist rather than a single pick-one — the
 * one thing `MenuSelect` cannot do, since every row there is mutually
 * exclusive. Portalled and positioned the same way, for the same reason: a
 * card that clips overflow for its own rounded corners would clip this too.
 */
export function ColumnPicker({
  label = "Columns",
  columns,
  isVisible,
  onToggle,
  className
}: {
  label?: string;
  columns: readonly ColumnDefinition[];
  isVisible: (key: string) => boolean;
  onToggle: (key: string) => void;
  className?: string;
}) {
  const listId = React.useId();
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
      if (!rect) return;
      setAnchor({ top: rect.bottom + 8, right: window.innerWidth - rect.right });
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
    <div ref={rootRef} className={cn("relative", className)}>
      <button
        type="button"
        ref={triggerRef}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        onClick={() => setOpen((current) => !current)}
        className="flex h-[var(--control-height-sm)] items-center gap-1.5 rounded-[8px] border border-hairline bg-surface-1 px-2.5 text-[length:var(--type-caption)] font-semibold text-muted-foreground transition hover:text-foreground"
      >
        <Columns3 aria-hidden className="h-3.5 w-3.5" strokeWidth={1.75} />
        {label}
      </button>

      {open && anchor ? createPortal(
        <div
          ref={menuRef}
          id={listId}
          role="menu"
          aria-label={`${label}: choose which columns to show`}
          style={{ position: "fixed", top: anchor.top, right: anchor.right }}
          className="z-[80] min-w-[220px] overflow-hidden rounded-xl border border-hairline/80 bg-popover p-1.5 shadow-lg dark:border-white/[0.07]"
        >
          {columns.map((column) => {
            const checked = isVisible(column.key);
            return (
              <button
                key={column.key}
                type="button"
                role="menuitemcheckbox"
                aria-checked={checked}
                onClick={() => onToggle(column.key)}
                className={cn(
                  "flex min-h-[var(--control-height-sm)] w-full items-center justify-between gap-3 rounded-lg px-3 text-left",
                  "text-[length:var(--shell-nav-size)] font-medium text-foreground transition hover:bg-muted/45"
                )}
              >
                <span className="truncate">{column.label}</span>
                {checked ? <Check aria-hidden className="h-3.5 w-3.5 shrink-0 text-primary" strokeWidth={2} /> : null}
              </button>
            );
          })}
        </div>,
        document.body
      ) : null}
    </div>
  );
}

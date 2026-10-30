import { useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { SlidersHorizontal } from "lucide-react";
import { cn } from "../../lib/utils";

/**
 * A Filter button whose menu holds the controls that narrow what a search asks (#1427). The menu opens under the
 * button, is never wider than the screen, and the button says how many filters are narrowing. The panel is
 * portalled (a card clips what overflows it); its sections never scroll on their own, only the panel does when the
 * screen is shorter than the choices.
 */
export function FilterMenu({ active, children, className }: { /** How many filters are narrowing right now. */ active: number; children: ReactNode; className?: string }) {
  const panelId = useId();
  const [open, setOpen] = useState(false);
  const [anchor, setAnchor] = useState<{ top: number; left: number; maxHeight: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const label = active > 0 ? `Filter, ${active} on` : "Filter";

  useEffect(() => {
    if (!open) return undefined;
    const onPointerDown = (event: PointerEvent) => {
      const target = event.target as Node;
      if (triggerRef.current?.contains(target) || panelRef.current?.contains(target)) return;
      setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      event.stopPropagation();
      setOpen(false);
      triggerRef.current?.focus();
    };
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown, true);
    };
  }, [open]);

  useLayoutEffect(() => {
    if (!open) return undefined;
    const measure = () => {
      const rect = triggerRef.current!.getBoundingClientRect();
      const wide = Math.min(576, window.innerWidth - 24);
      const top = rect.bottom + 8;
      setAnchor({
        top,
        left: Math.max(12, Math.min(rect.right - wide, window.innerWidth - 12 - wide)),
        maxHeight: Math.max(160, window.innerHeight - top - 12)
      });
    };
    measure();
    window.addEventListener("resize", measure);
    window.addEventListener("scroll", measure, true);
    return () => {
      window.removeEventListener("resize", measure);
      window.removeEventListener("scroll", measure, true);
    };
  }, [open]);

  useEffect(() => {
    if (open && anchor) panelRef.current?.focus();
  }, [open, anchor]);

  return (
    <div className={cn("relative shrink-0", className)}>
      <button
        ref={triggerRef}
        type="button"
        aria-haspopup="true"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        aria-label={label}
        onClick={() => setOpen((current) => !current)}
        className={cn(
          "inline-flex h-12 items-center gap-2 whitespace-nowrap rounded-xl border border-hairline bg-surface-1 px-4 text-(length:--type-control) font-medium text-muted-foreground hover:text-foreground",
          "focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring",
          active > 0 && "border-primary/40 bg-primary/10 text-primary hover:text-primary"
        )}
      >
        <SlidersHorizontal aria-hidden className="h-4 w-4" />
        Filter
        {active > 0 ? <span aria-hidden className="tabular-nums">{active}</span> : null}
      </button>
      {open && anchor
        ? createPortal(
            <div
              ref={panelRef}
              id={panelId}
              role="group"
              aria-label="Search filters"
              tabIndex={-1}
              style={{ position: "fixed", top: anchor.top, left: anchor.left, width: "min(36rem, calc(100vw - 24px))", maxHeight: anchor.maxHeight }}
              className="z-80 grid gap-(--grid-gap) overflow-y-auto rounded-xl border border-hairline/80 bg-popover p-(--card-pad-x) shadow-lg focus-visible:outline-hidden dark:border-white/[0.07]"
            >
              {children}
            </div>,
            document.body
          )
        : null}
    </div>
  );
}

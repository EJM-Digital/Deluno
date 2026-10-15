import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { SlidersHorizontal } from "lucide-react";
import { cn } from "../../lib/utils";

export interface FilterChipOption<T extends string> {
  value: T;
  label: string;
  /** Shown after the label; left off where the number is not known. A "3/4" reads as passed of all. */
  count?: number | string;
  /** A status colour, drawn as a dot before the label (the Dashboard's Health areas: red, amber, green). */
  dot?: string;
  /** Said on hover, where the chip is short for something longer. */
  title?: string;
}

/**
 * The one filter control in Deluno (James, 2 Oct 2026: "chips with counts", drawn as Weir draws its History's): a joined
 * group, 3px inside a 12px-round box, of 30px options 14px apart at the sides, 13px/500, each with how many it holds;
 * the chosen one tinted in the accent colour. Activity's status, Movies · TV on the setup lists and the Indexers' On /
 * Off all wear it, so a filter looks the same wherever it is, and the same as in Weir.
 */
export function FilterChips<T extends string>({
  label,
  value,
  onValueChange,
  options,
  size = "md",
  className
}: {
  /** What the group chooses, said to screen readers ("Show", "Filter by media type"). */
  label: string;
  value: T;
  onValueChange: (value: T) => void;
  options: readonly FilterChipOption<T>[];
  /**
   * "md" on the header's title line (38px box, 30px options, 13px); "sm" in a card's header (28px box, 22px options,
   * 12px), so a card filter never makes its header taller than the card's title (#1115).
   */
  size?: "md" | "sm";
  className?: string;
}) {
  return (
    <div
      role="radiogroup"
      aria-label={label}
      className={cn("no-scrollbar flex w-max min-w-0 max-w-full shrink items-center gap-[2px] overflow-x-auto border border-hairline bg-card", size === "sm" ? "rounded-[9px] p-[2px]" : "rounded-xl p-[3px]", className)}
    >
      {options.map((option) => {
        const active = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={active}
            title={option.title}
            aria-label={option.count !== undefined ? `${option.label} ${option.count}` : option.label}
            onClick={(event) => {
              onValueChange(option.value);
              // A chip the box has cut off is brought into view when it is chosen.
              event.currentTarget.scrollIntoView?.({ inline: "nearest", block: "nearest" });
            }}
            className={cn(
              "inline-flex shrink-0 items-center whitespace-nowrap border font-medium transition-colors duration-150 motion-reduce:transition-none",
              size === "sm" ? "h-[22px] rounded-[7px] px-[10px] text-(length:--type-caption)" : "h-[30px] rounded-[10px] px-[14px] text-(length:--type-control)",
              "focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring",
              active ? "border-primary/40 bg-primary/10 text-primary" : "border-transparent text-muted-foreground hover:bg-surface-2 hover:text-foreground"
            )}
          >
            {option.dot ? <span aria-hidden="true" className="mr-[5px] h-[7px] w-[7px] shrink-0 rounded-full" style={{ background: option.dot }} /> : null}
            {option.label}
            {option.count !== undefined ? <span className={cn("ml-[5px] tabular-nums", active ? "opacity-80" : "text-muted-foreground")}>{option.count}</span> : null}
          </button>
        );
      })}
    </div>
  );
}

/** The line under a card's header that holds a list's rarer filters (Activity's kind, source and time; Indexers' protocol). */
export const FILTER_ROW_CLASS = "flex flex-wrap items-center gap-2 border-b border-hairline px-(--card-pad-x) py-2";

/** The trigger of a dropdown filter on that line, for `MenuSelect`'s `triggerClassName`. */
export const FILTER_MENU_TRIGGER =
  "h-[28px] rounded-[8px] border border-hairline bg-surface-1 px-2.5 text-(length:--type-caption) font-semibold text-muted-foreground hover:text-foreground";

export interface MoreFilterGroup {
  /** What the group narrows by, said above its chips in the panel ("Step", "Time"). */
  label: string;
  /** Names the chips to screen readers ("Filter by step"). */
  ariaLabel: string;
  value: string;
  onValueChange: (value: string) => void;
  options: readonly FilterChipOption<string>[];
  /** The value that does not narrow ("Any step"). Defaults to the first option. */
  neutral?: string;
}

/** How many of the groups are narrowing right now. */
export function activeMoreFilters(groups: readonly MoreFilterGroup[]): number {
  return groups.filter((group) => group.value !== (group.neutral ?? group.options[0]?.value ?? "")).length;
}

/**
 * The rarer filters of a list behind one button beside its `FilterChips` (#1168): the common choice stays a chip,
 * and the ones used now and then (a step, a time, a protocol, a severity) open from "More filters" as groups of the
 * same chips, so every filter in Deluno is the one control. The button says how many are narrowing the list.
 */
export function MoreFilters({ groups, size = "sm", className }: { groups: readonly MoreFilterGroup[]; /** "md" on the header's title line (the height of its search box), "sm" in a card's header. */ size?: "md" | "sm"; className?: string }) {
  const panelId = useId();
  const [open, setOpen] = useState(false);
  const [anchor, setAnchor] = useState<{ top: number; left: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const active = activeMoreFilters(groups);
  const triggerLabel = active > 0 ? `More filters, ${active} on` : "More filters";

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
      const rect = triggerRef.current?.getBoundingClientRect();
      if (!rect) return;
      // The panel is 28rem wide at most and never wider than the screen; it opens under the button and is pulled back inside.
      const wide = Math.min(448, window.innerWidth - 24);
      setAnchor({ top: rect.bottom + 8, left: Math.max(12, Math.min(rect.right - wide, window.innerWidth - 12 - wide)) });
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
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        aria-label={triggerLabel}
        onClick={() => setOpen((current) => !current)}
        className={cn(
          size === "sm" ? FILTER_MENU_TRIGGER : "h-9.5 rounded-xl border border-hairline bg-card px-3 text-(length:--type-control) font-medium text-muted-foreground hover:text-foreground",
          "inline-flex items-center gap-1.5 whitespace-nowrap focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring",
          active > 0 && "border-primary/40 bg-primary/10 text-primary hover:text-primary"
        )}
      >
        <SlidersHorizontal aria-hidden className="h-3.5 w-3.5" />
        More filters
        {active > 0 ? <span aria-hidden className="tabular-nums">{active}</span> : null}
      </button>
      {open && anchor
        ? createPortal(
            <div
              ref={panelRef}
              id={panelId}
              role="dialog"
              aria-label="More filters"
              tabIndex={-1}
              style={{ position: "fixed", top: anchor.top, left: anchor.left, width: "min(28rem, calc(100vw - 24px))" }}
              className="z-80 grid gap-3 rounded-xl border border-hairline/80 bg-popover p-3 shadow-lg focus-visible:outline-hidden dark:border-white/[0.07]"
            >
              {groups.map((group) => (
                <div key={group.ariaLabel} className="grid gap-1.5">
                  <p className="text-(length:--type-micro) font-semibold uppercase tracking-widest text-muted-foreground">{group.label}</p>
                  <FilterChips<string>
                    label={group.ariaLabel}
                    size="sm"
                    value={group.value}
                    onValueChange={group.onValueChange}
                    options={group.options}
                    className="w-full flex-wrap overflow-visible"
                  />
                </div>
              ))}
            </div>,
            document.body
          )
        : null}
    </div>
  );
}

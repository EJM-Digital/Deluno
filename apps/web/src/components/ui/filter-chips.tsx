import { cn } from "../../lib/utils";

export interface FilterChipOption<T extends string> {
  value: T;
  label: string;
  /** Shown after the label; left off where the number is not known. */
  count?: number;
}

/**
 * The one filter control in Deluno (the owner, 2 Oct 2026: "chips with counts", drawn as Weir draws its History's): a joined
 * group, 3px inside a 12px-round box, of 30px options 14px apart at the sides, 13px/500, each with how many it holds;
 * the chosen one tinted in the accent colour. Activity's status, Movies · TV on the setup lists and the Indexers' On /
 * Off all wear it, so a filter looks the same wherever it is, and the same as in Weir.
 */
export function FilterChips<T extends string>({
  label,
  value,
  onValueChange,
  options,
  className
}: {
  /** What the group chooses, said to screen readers ("Show", "Filter by media type"). */
  label: string;
  value: T;
  onValueChange: (value: T) => void;
  options: readonly FilterChipOption<T>[];
  className?: string;
}) {
  return (
    <div
      role="radiogroup"
      aria-label={label}
      className={cn("no-scrollbar flex w-max min-w-0 max-w-full shrink items-center gap-[2px] overflow-x-auto rounded-[12px] border border-hairline bg-card p-[3px]", className)}
    >
      {options.map((option) => {
        const active = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={active}
            aria-label={option.count !== undefined ? `${option.label} ${option.count}` : option.label}
            onClick={() => onValueChange(option.value)}
            className={cn(
              "inline-flex h-[30px] shrink-0 items-center whitespace-nowrap rounded-[10px] border px-[14px] text-[13px] font-medium transition-colors duration-150 motion-reduce:transition-none",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
              active ? "border-primary/40 bg-primary/10 text-primary" : "border-transparent text-muted-foreground hover:bg-surface-2 hover:text-foreground"
            )}
          >
            {option.label}
            {option.count !== undefined ? <span className={cn("ml-[5px] tabular-nums", active ? "opacity-80" : "text-muted-foreground/70")}>{option.count}</span> : null}
          </button>
        );
      })}
    </div>
  );
}

/** The line under a card's header that holds a list's rarer filters (Activity's kind, source and time; Indexers' protocol). */
export const FILTER_ROW_CLASS = "flex flex-wrap items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2";

/** The trigger of a dropdown filter on that line, for `MenuSelect`'s `triggerClassName`. */
export const FILTER_MENU_TRIGGER =
  "h-[var(--control-height-sm)] rounded-[8px] border border-hairline bg-surface-1 px-2.5 text-[length:var(--type-caption)] font-semibold text-muted-foreground hover:text-foreground";

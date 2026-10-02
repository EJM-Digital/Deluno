import { cn } from "../../lib/utils";

export interface FilterChipOption<T extends string> {
  value: T;
  label: string;
  /** Shown after the label; left off where the number is not known. */
  count?: number;
}

/**
 * The one filter control for a list's card header (the owner, 2 Oct 2026: "chips with counts"): rounded chips, the chosen
 * one in the accent colour, each with how many it holds. Activity's status, Movies · TV on the setup lists and the
 * Indexers' On / Off all wear it, so a filter looks the same wherever it is.
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
    <div role="radiogroup" aria-label={label} className={cn("no-scrollbar flex min-w-0 items-center gap-1.5 overflow-x-auto", className)}>
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
              "inline-flex h-7 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full border px-3 text-[length:var(--type-caption)] font-semibold transition-colors",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
              active ? "border-primary/40 bg-primary/12 text-primary" : "border-hairline bg-surface-1 text-muted-foreground hover:text-foreground"
            )}
          >
            {option.label}
            {option.count !== undefined ? <span className="tabular-nums opacity-80">{option.count}</span> : null}
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

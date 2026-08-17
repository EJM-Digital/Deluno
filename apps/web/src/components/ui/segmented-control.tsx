import * as React from "react";
import { cn } from "../../lib/utils";
import { useFieldContext } from "./field";

export interface SegmentedOption<T extends string> {
  value: T;
  label: React.ReactNode;
  disabled?: boolean;
}

interface SegmentedControlProps<T extends string> {
  value: T;
  onValueChange: (value: T) => void;
  options: SegmentedOption<T>[];
  /** Accessible name when not wrapped in a Field. */
  "aria-label"?: string;
  className?: string;
  disabled?: boolean;
}

/**
 * A small exclusive choice (2–4 options) rendered as one 36px control.
 * Real radio semantics: `role="radiogroup"` with roving arrow-key focus.
 */
export function SegmentedControl<T extends string>({
  value,
  onValueChange,
  options,
  className,
  disabled,
  ...rest
}: SegmentedControlProps<T>) {
  const field = useFieldContext();
  const refs = React.useRef<Array<HTMLButtonElement | null>>([]);

  function focusIndex(index: number) {
    const enabled = options.map((option, i) => (option.disabled ? -1 : i)).filter((i) => i >= 0);
    if (!enabled.length) return;
    const wrapped = ((index % options.length) + options.length) % options.length;
    const next = enabled.includes(wrapped) ? wrapped : enabled.find((i) => i > wrapped) ?? enabled[0]!;
    refs.current[next]?.focus();
    onValueChange(options[next]!.value);
  }

  return (
    <div
      role="radiogroup"
      id={field?.id}
      aria-labelledby={rest["aria-label"] ? undefined : field?.labelId}
      aria-label={rest["aria-label"]}
      aria-describedby={field?.describedBy}
      className={cn(
        "flex h-[var(--control-height)] w-full overflow-hidden rounded-[10px] border border-hairline bg-surface-1",
        disabled && "opacity-60",
        className
      )}
    >
      {options.map((option, index) => {
        const active = option.value === value;
        return (
          <button
            key={option.value}
            ref={(node) => {
              refs.current[index] = node;
            }}
            type="button"
            role="radio"
            aria-checked={active}
            tabIndex={active ? 0 : -1}
            disabled={disabled || option.disabled}
            onClick={() => onValueChange(option.value)}
            onKeyDown={(event) => {
              if (event.key === "ArrowRight" || event.key === "ArrowDown") {
                event.preventDefault();
                focusIndex(index + 1);
              } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
                event.preventDefault();
                focusIndex(index - 1);
              }
            }}
            className={cn(
              "density-control-text flex flex-1 items-center justify-center gap-1.5 border-r border-hairline px-3 font-medium transition-colors last:border-r-0",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
              "disabled:cursor-not-allowed",
              active ? "bg-primary/12 text-primary" : "text-muted-foreground hover:bg-surface-2 hover:text-foreground"
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

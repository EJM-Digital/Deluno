import * as React from "react";
import { cn } from "../../lib/utils";
import { useFieldContext } from "./field";
import { BOXED_SWITCH_CLASS, BOXED_SWITCH_OFF_CLASS, BOXED_SWITCH_ON_CLASS, BOXED_SWITCH_OPTION_CLASS } from "./filter-chips";

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
 * A small exclusive choice (2–4 options) drawn as the boxed switch (the same look as FilterChips, #1530).
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

  /** Moves to the nearest enabled option in the direction pressed, wrapping round the ends. */
  function focusFrom(index: number, step: 1 | -1) {
    const count = options.length;
    const order = options.map((_, k) => (((index + step * (k + 1)) % count) + count) % count);
    const next = order.find((i) => !options[i]!.disabled)!;
    refs.current[next]!.focus();
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
        "no-scrollbar flex w-full min-w-0 items-center overflow-x-auto sm:w-max sm:max-w-full",
        BOXED_SWITCH_CLASS,
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
                focusFrom(index, 1);
              } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
                event.preventDefault();
                focusFrom(index, -1);
              }
            }}
            className={cn(
              BOXED_SWITCH_OPTION_CLASS,
              "gap-1.5 disabled:cursor-not-allowed",
              active ? BOXED_SWITCH_ON_CLASS : BOXED_SWITCH_OFF_CLASS
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

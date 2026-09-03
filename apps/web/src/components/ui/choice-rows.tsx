import * as React from "react";
import { cn } from "../../lib/utils";
import { useFieldContext } from "./field";

export interface ChoiceRowOption<T extends string> {
  value: T;
  label: React.ReactNode;
  /** What choosing this actually does, in one line. */
  description?: React.ReactNode;
  disabled?: boolean;
}

/**
 * An exclusive choice where the consequences matter more than the labels.
 *
 * <p>`SegmentedControl` is the right control for two to four short options on
 * one 36px line. This is for the case it cannot serve: options whose labels do
 * not fit side by side, and whose meaning is not obvious from the label alone.
 * A dropdown hides every option but one until you open it, which is the wrong
 * trade when the whole question is "what would each of these do?" — so every
 * option is on screen with its consequence beside it.</p>
 *
 * <p>Real radio semantics: one tab stop for the group, arrow keys within it.</p>
 */
export function ChoiceRows<T extends string>({
  value,
  onValueChange,
  options,
  className,
  disabled,
  ...rest
}: {
  value: T;
  onValueChange: (value: T) => void;
  options: ChoiceRowOption<T>[];
  "aria-label"?: string;
  className?: string;
  disabled?: boolean;
}) {
  const field = useFieldContext();
  const refs = React.useRef<Array<HTMLButtonElement | null>>([]);
  const selectable = options.filter((option) => !option.disabled);

  function move(delta: number) {
    if (!selectable.length) return;
    const current = selectable.findIndex((option) => option.value === value);
    const nextIndex = ((current + delta) % selectable.length + selectable.length) % selectable.length;
    const next = selectable[nextIndex]!;
    onValueChange(next.value);
    refs.current[options.indexOf(next)]?.focus();
  }

  return (
    <div
      role="radiogroup"
      id={field?.id}
      aria-labelledby={rest["aria-label"] ? undefined : field?.labelId}
      aria-label={rest["aria-label"]}
      aria-describedby={field?.describedBy}
      className={cn("grid gap-1", disabled && "opacity-60", className)}
    >
      {options.map((option, index) => {
        const active = option.value === value;
        return (
          <button
            key={option.value}
            ref={(node) => { refs.current[index] = node; }}
            type="button"
            role="radio"
            aria-checked={active}
            // One tab stop for the whole group, arrows inside it — the shape a
            // screen reader announces as a set of N rather than N buttons.
            tabIndex={active ? 0 : -1}
            disabled={disabled || option.disabled}
            onClick={() => onValueChange(option.value)}
            onKeyDown={(event) => {
              if (event.key === "ArrowDown" || event.key === "ArrowRight") {
                event.preventDefault();
                move(1);
              } else if (event.key === "ArrowUp" || event.key === "ArrowLeft") {
                event.preventDefault();
                move(-1);
              }
            }}
            className={cn(
              "flex w-full items-start gap-[var(--grid-gap)] rounded-[10px] border px-[var(--field-pad-x)] py-2 text-left transition-colors",
              "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
              "disabled:cursor-not-allowed disabled:opacity-50",
              active
                ? "border-primary/60 bg-primary/[0.08]"
                : "border-hairline bg-surface-1 hover:bg-surface-2/60"
            )}
          >
            <span
              aria-hidden
              className={cn(
                "mt-[3px] grid h-3.5 w-3.5 shrink-0 place-items-center rounded-full border transition-colors",
                active ? "border-primary" : "border-hairline"
              )}
            >
              <span className={cn("h-1.5 w-1.5 rounded-full", active ? "bg-primary" : "bg-transparent")} />
            </span>
            <span className="min-w-0">
              <span className="block text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">
                {option.label}
              </span>
              {option.description ? (
                <span className="mt-0.5 block text-[length:var(--type-caption)] leading-snug text-muted-foreground">
                  {option.description}
                </span>
              ) : null}
            </span>
          </button>
        );
      })}
    </div>
  );
}

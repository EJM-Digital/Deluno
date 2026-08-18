import * as React from "react";
import { cn } from "../../lib/utils";
import { useFieldContext } from "./field";

interface SwitchProps extends Omit<React.ButtonHTMLAttributes<HTMLButtonElement>, "onChange"> {
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  size?: "default" | "sm";
}

/**
 * The one toggle. `role="switch"` + `aria-checked`, keyboard-operable, and it
 * picks up its label from the surrounding `Field` or `SwitchRow`.
 */
const Switch = React.forwardRef<HTMLButtonElement, SwitchProps>(
  ({ checked, onCheckedChange, className, id, disabled, size = "default", ...props }, ref) => {
    const field = useFieldContext();
    const sm = size === "sm";
    return (
      <button
        ref={ref}
        type="button"
        role="switch"
        id={id ?? field?.id}
        aria-checked={checked}
        aria-describedby={props["aria-describedby"] ?? field?.describedBy}
        disabled={disabled}
        onClick={(event) => {
          event.stopPropagation();
          onCheckedChange(!checked);
        }}
        className={cn(
          "relative inline-flex shrink-0 items-center rounded-full border transition-colors duration-150",
          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
          "disabled:cursor-not-allowed disabled:opacity-50",
          sm ? "h-[18px] w-[30px]" : "h-5 w-[34px]",
          checked ? "border-primary bg-primary" : "border-hairline bg-surface-3",
          className
        )}
        {...props}
      >
        <span
          aria-hidden
          className={cn(
            "block rounded-full transition-transform duration-150",
            sm ? "h-3 w-3 translate-x-[2px]" : "h-3.5 w-3.5 translate-x-[2px]",
            checked
              ? cn("bg-primary-foreground", sm ? "translate-x-[13px]" : "translate-x-[15px]")
              : "bg-foreground/50"
          )}
        />
      </button>
    );
  }
);
Switch.displayName = "Switch";

/**
 * Label + one-line description on the left, switch on the right. Used for
 * on/off decisions inside drawers and settings sections.
 */
export function SwitchRow({
  label,
  description,
  checked,
  onCheckedChange,
  disabled,
  className
}: {
  label: React.ReactNode;
  description?: React.ReactNode;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  disabled?: boolean;
  className?: string;
}) {
  const id = React.useId();
  return (
    <div className={cn("flex min-h-[var(--control-height)] items-center justify-between gap-[var(--grid-gap)]", className)}>
      <label htmlFor={id} className="min-w-0 cursor-pointer">
        <span className="block text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">{label}</span>
        {description ? (
          <span className="mt-0.5 block text-[length:var(--type-caption)] leading-snug text-muted-foreground">{description}</span>
        ) : null}
      </label>
      <Switch id={id} checked={checked} onCheckedChange={onCheckedChange} disabled={disabled} />
    </div>
  );
}

export { Switch };

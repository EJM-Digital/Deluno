import * as React from "react";
import { Check } from "lucide-react";
import { cn } from "../../lib/utils";
import { useFieldContext } from "./field";

interface CheckboxProps extends Omit<React.ComponentProps<"input">, "checked" | "onChange" | "type"> {
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  indeterminate?: boolean;
}

/** Native checkbox with Deluno's shared focus treatment and Field wiring. */
const Checkbox = React.forwardRef<HTMLInputElement, CheckboxProps>(
  ({ checked, onCheckedChange, indeterminate = false, className, id, disabled, ...props }, ref) => {
    const field = useFieldContext();
    const inputRef = React.useRef<HTMLInputElement>(null);
    React.useImperativeHandle(ref, () => inputRef.current as HTMLInputElement);

    React.useEffect(() => {
      if (inputRef.current) inputRef.current.indeterminate = indeterminate;
    }, [indeterminate]);

    return (
      <span className="relative inline-flex h-4 w-4 shrink-0">
        <input
          ref={inputRef}
          type="checkbox"
          id={id ?? field?.id}
          checked={checked}
          disabled={disabled}
          aria-describedby={props["aria-describedby"] ?? field?.describedBy}
          aria-invalid={props["aria-invalid"] ?? (field?.invalid ? true : undefined)}
          onChange={(event) => onCheckedChange(event.target.checked)}
          className={cn(
            "peer h-4 w-4 appearance-none rounded border border-hairline bg-surface-1 shadow-none",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-background",
            "checked:border-primary checked:bg-primary indeterminate:border-primary indeterminate:bg-primary",
            "disabled:cursor-not-allowed disabled:opacity-60",
            className
          )}
          {...props}
        />
        <Check aria-hidden className="pointer-events-none absolute inset-0 m-auto hidden h-3 w-3 text-primary-foreground peer-checked:block peer-indeterminate:block" />
      </span>
    );
  }
);
Checkbox.displayName = "Checkbox";

export function CheckboxRow({
  label,
  description,
  checked,
  onCheckedChange,
  disabled,
  id: providedId,
  className
}: {
  label: React.ReactNode;
  description?: React.ReactNode;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  disabled?: boolean;
  id?: string;
  className?: string;
}) {
  const generatedId = React.useId();
  const id = providedId ?? generatedId;
  return (
    <div className={cn("flex min-h-[var(--control-height)] items-start gap-2", className)}>
      <Checkbox id={id} checked={checked} onCheckedChange={onCheckedChange} disabled={disabled} />
      <label htmlFor={id} className="min-w-0 cursor-pointer">
        <span className="block text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">{label}</span>
        {description ? <span className="mt-0.5 block text-[length:var(--type-caption)] leading-snug text-muted-foreground">{description}</span> : null}
      </label>
    </div>
  );
}

export { Checkbox };

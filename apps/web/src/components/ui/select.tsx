import * as React from "react";
import { ChevronDown } from "lucide-react";
import { cn } from "../../lib/utils";
import { useFieldContext } from "./field";
import { controlClassName } from "./input";

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean;
}

interface SelectProps extends Omit<React.ComponentProps<"select">, "children"> {
  options?: SelectOption[];
  /** Shown as the first, empty-value option. */
  placeholder?: string;
  children?: React.ReactNode;
}

/**
 * Native `<select>` in Deluno's control chrome. Native on purpose: keyboard,
 * screen-reader and mobile behaviour come for free, and it lines up with Input.
 */
const Select = React.forwardRef<HTMLSelectElement, SelectProps>(
  ({ className, id, options, placeholder, children, ...props }, ref) => {
    const field = useFieldContext();
    return (
      <span className="group relative block w-full min-w-0">
        <select
          ref={ref}
          id={id ?? field?.id}
          aria-describedby={props["aria-describedby"] ?? field?.describedBy}
          aria-invalid={props["aria-invalid"] ?? (field?.invalid ? true : undefined)}
          className={cn(
            controlClassName,
            "cursor-pointer appearance-none pr-[calc(var(--field-pad-x)+1.5rem)] hover:border-foreground/20 hover:bg-surface-2 focus:bg-surface-2",
            className
          )}
          {...props}
        >
          {placeholder !== undefined ? <option value="">{placeholder}</option> : null}
          {options?.map((option) => (
            <option key={option.value} value={option.value} disabled={option.disabled}>
              {option.label}
            </option>
          ))}
          {children}
        </select>
        <ChevronDown
          aria-hidden
          className="pointer-events-none absolute right-[var(--field-pad-x)] top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/80 transition-colors group-hover:text-foreground group-focus-within:text-primary"
        />
      </span>
    );
  }
);

Select.displayName = "Select";

export { Select };

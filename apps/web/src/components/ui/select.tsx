import * as React from "react";
import { ChevronDown } from "lucide-react";
import { cn } from "../../lib/utils";
import { useFieldContext } from "./field";
import { bareControlClassName, controlClassName } from "./input";

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean;
}

interface SelectProps extends Omit<React.ComponentProps<"select">, "children"> {
  options?: SelectOption[];
  /** Shown as the first, empty-value option. */
  placeholder?: string;
  /**
   * Whether to wear Deluno's control box. Turn it off where the select already
   * sits inside chrome of its own — a toolbar pill, say — so it does not draw a
   * second rounded box inside the first on hover and focus.
   */
  chrome?: boolean;
  children?: React.ReactNode;
}

/**
 * Width classes size the box the select and its chevron share, not the select alone. On the select alone a
 * `w-auto` filter in a row of filters sat in a full-width box: each took a line of its own and its chevron sat at the
 * far edge of that line, away from the control (James, 2 Oct 2026: the Indexers filters).
 */
const BOX_CLASS = /^(?:[a-z0-9-]+:)*!?(?:w-|min-w-|max-w-|flex-|shrink|grow|basis-|self-)/;

function splitBoxClasses(className: string | undefined): [box: string, control: string] {
  const box: string[] = [];
  const control: string[] = [];
  for (const token of (className ?? "").split(/\s+/).filter(Boolean)) (BOX_CLASS.test(token) ? box : control).push(token);
  return [box.join(" "), control.join(" ")];
}

/**
 * Native `<select>` in Deluno's control chrome. Native on purpose: keyboard,
 * screen-reader and mobile behaviour come for free, and it lines up with Input.
 */
const Select = React.forwardRef<HTMLSelectElement, SelectProps>(
  ({ className, id, options, placeholder, chrome = true, children, ...props }, ref) => {
    const field = useFieldContext();
    const [boxClassName, controlClasses] = splitBoxClasses(className);
    return (
      <span className={cn("group relative block w-full min-w-0", boxClassName)}>
        <select
          ref={ref}
          id={id ?? field?.id}
          aria-describedby={props["aria-describedby"] ?? field?.describedBy}
          aria-invalid={props["aria-invalid"] ?? (field?.invalid ? true : undefined)}
          className={cn(
            chrome ? controlClassName : bareControlClassName,
            // The right padding is the gutter the chevron below sits in, so it
            // belongs to the control whether or not the box does.
            "cursor-pointer appearance-none pr-[calc(var(--field-pad-x)+1.5rem)]",
            chrome && "hover:border-foreground/20 hover:bg-surface-2 focus:bg-surface-2",
            controlClasses
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
          className="pointer-events-none absolute right-(--field-pad-x) top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground transition-colors group-hover:text-foreground group-focus-within:text-primary"
        />
      </span>
    );
  }
);

Select.displayName = "Select";

export { Select };

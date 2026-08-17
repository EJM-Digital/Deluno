import * as React from "react";
import { cn } from "../../lib/utils";
import { useFieldContext } from "./field";

/** Shared control chrome for Input, Select and Textarea — one look, one height. */
export const controlClassName =
  "density-control-text flex h-[var(--control-height)] w-full rounded-[10px] border border-hairline bg-surface-1 px-[var(--field-pad-x)] py-2 text-foreground shadow-none placeholder:text-muted-foreground/80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-background disabled:cursor-not-allowed disabled:opacity-60 aria-[invalid=true]:border-destructive/60";

const Input = React.forwardRef<HTMLInputElement, React.ComponentProps<"input">>(
  ({ className, id, ...props }, ref) => {
    const field = useFieldContext();
    return (
      <input
        ref={ref}
        id={id ?? field?.id}
        aria-describedby={props["aria-describedby"] ?? field?.describedBy}
        aria-invalid={props["aria-invalid"] ?? (field?.invalid ? true : undefined)}
        className={cn(controlClassName, className)}
        {...props}
      />
    );
  }
);

Input.displayName = "Input";

export { Input };

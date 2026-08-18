import * as React from "react";
import { cn } from "../../lib/utils";
import { useFieldContext } from "./field";
import { controlClassName } from "./input";

const Textarea = React.forwardRef<HTMLTextAreaElement, React.ComponentProps<"textarea">>(
  ({ className, id, rows = 3, ...props }, ref) => {
    const field = useFieldContext();
    return (
      <textarea
        ref={ref}
        id={id ?? field?.id}
        rows={rows}
        aria-describedby={props["aria-describedby"] ?? field?.describedBy}
        aria-invalid={props["aria-invalid"] ?? (field?.invalid ? true : undefined)}
        className={cn(controlClassName, "h-auto min-h-[calc(var(--control-height)*2)] resize-y leading-relaxed", className)}
        {...props}
      />
    );
  }
);

Textarea.displayName = "Textarea";

export { Textarea };

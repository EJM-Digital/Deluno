import * as React from "react";
import { cn } from "../../lib/utils";

const Input = React.forwardRef<HTMLInputElement, React.ComponentProps<"input">>(
  ({ className, ...props }, ref) => {
    return (
      <input
        ref={ref}
        className={cn(
          "density-control-text flex h-[var(--control-height)] w-full rounded-[10px] border border-hairline bg-surface-1 px-[var(--field-pad-x)] py-2 text-foreground shadow-none placeholder:text-muted-foreground/80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1 focus-visible:ring-offset-background",
          className
        )}
        {...props}
      />
    );
  }
);

Input.displayName = "Input";

export { Input };

import * as React from "react";
import { cn } from "../../lib/utils";

export function Kbd({ className, ...props }: React.HTMLAttributes<HTMLElement>) {
  return (
    <kbd
      className={cn(
        "inline-flex min-h-[calc(var(--control-height-sm)*0.62)] min-w-[calc(var(--control-height-sm)*0.62)] items-center justify-center rounded-md border border-hairline bg-surface-2 px-1.5 font-mono text-[length:var(--type-micro)] font-medium text-muted-foreground",
        className
      )}
      {...props}
    />
  );
}

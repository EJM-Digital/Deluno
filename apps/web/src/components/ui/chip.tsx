import * as React from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "../../lib/utils";

/**
 * Chip — the one status pill. Dot + short label, semantic tone.
 * Use for health/state in list rows and drawer key/value lines.
 * (Badge remains for small square-ish tags such as media type.)
 */
const chipVariants = cva(
  "inline-flex h-6 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full border px-2.5 text-[length:var(--type-caption)] font-semibold leading-none",
  {
    variants: {
      tone: {
        ok: "border-success/30 bg-success/10 text-success",
        warn: "border-warning/30 bg-warning/10 text-warning",
        bad: "border-destructive/30 bg-destructive/10 text-destructive",
        info: "border-info/30 bg-info/10 text-info",
        muted: "border-hairline bg-surface-2 text-muted-foreground"
      }
    },
    defaultVariants: { tone: "muted" }
  }
);

export interface ChipProps extends React.HTMLAttributes<HTMLSpanElement>, VariantProps<typeof chipVariants> {
  dot?: boolean;
}

export function Chip({ className, tone, dot = true, children, ...props }: ChipProps) {
  return (
    <span className={cn(chipVariants({ tone }), className)} {...props}>
      {dot ? <span aria-hidden className="h-1.5 w-1.5 rounded-full bg-current" /> : null}
      {children}
    </span>
  );
}

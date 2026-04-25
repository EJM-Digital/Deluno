import { cva, type VariantProps } from "class-variance-authority";
import * as React from "react";
import { cn } from "../../lib/utils";

const badgeVariants = cva(
  "inline-flex items-center gap-1 rounded-md border px-2 py-0.5 text-[11px] font-semibold leading-none tracking-tight",
  {
    variants: {
      variant: {
        default: "border-hairline bg-muted/40 text-foreground",
        success: "border-success/30 bg-success/14 text-success",
        warning: "border-warning/35 bg-warning/14 text-warning",
        destructive: "border-destructive/35 bg-destructive/14 text-destructive",
        info: "border-info/30 bg-info/14 text-info"
      }
    },
    defaultVariants: {
      variant: "default"
    }
  }
);

export interface BadgeProps
  extends React.HTMLAttributes<HTMLDivElement>,
    VariantProps<typeof badgeVariants> {}

export function Badge({ className, variant, ...props }: BadgeProps) {
  return <div className={cn(badgeVariants({ variant }), className)} {...props} />;
}

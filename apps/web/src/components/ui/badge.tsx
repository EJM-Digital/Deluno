import { cva, type VariantProps } from "class-variance-authority";
import * as React from "react";
import { cn } from "../../lib/utils";

const badgeVariants = cva(
  "inline-flex items-center gap-1 rounded-md border px-2 py-0.5 text-[11px] font-medium leading-none tracking-tight",
  {
    variants: {
      variant: {
        default: "border-hairline bg-muted/40 text-foreground",
        success: "border-success/15 bg-success/10 text-success",
        warning: "border-warning/15 bg-warning/10 text-warning",
        destructive: "border-destructive/15 bg-destructive/10 text-destructive",
        info: "border-info/15 bg-info/10 text-info"
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

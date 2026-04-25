import * as React from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { Slot } from "@radix-ui/react-slot";
import { cn } from "../../lib/utils";

const buttonVariants = cva(
  [
    "inline-flex items-center justify-center gap-2 rounded-[10px] text-[length:var(--button-font-size)] font-medium leading-none",
    "transition-all duration-150 ease-out",
    "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
    "disabled:pointer-events-none disabled:opacity-40 active:scale-[0.975]"
  ].join(" "),
  {
    variants: {
      variant: {
        default: [
          "relative overflow-hidden",
          "bg-gradient-to-b from-primary to-[hsl(var(--primary-2))]",
          "dark:from-primary dark:to-[hsl(var(--primary-2))]",
          "text-primary-foreground",
          "shadow-[0_1px_2px_hsl(var(--primary-deep)/0.35),inset_0_1px_0_hsl(0_0%_100%/0.12)]",
          "hover:brightness-110"
        ].join(" "),
        secondary:
          "bg-secondary text-secondary-foreground border border-hairline shadow-[var(--shadow-card)] hover:bg-surface-3",
        ghost: "text-muted-foreground hover:bg-secondary hover:text-foreground",
        outline:
          "border border-hairline bg-transparent text-foreground shadow-[var(--shadow-card)] hover:bg-secondary"
      },
      size: {
        default: "h-[var(--control-height)] px-4 py-2",
        sm: "h-[var(--control-height-sm)] px-3 text-[length:var(--button-font-size-sm)]",
        lg: "h-[var(--control-height-lg)] px-6",
        icon: "h-[var(--control-height-icon)] w-[var(--control-height-icon)] shrink-0"
      }
    },
    defaultVariants: {
      variant: "default",
      size: "default"
    }
  }
);

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  asChild?: boolean;
}

const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, asChild = false, ...props }, ref) => {
    const Comp = asChild ? Slot : "button";
    return (
      <Comp
        className={cn(buttonVariants({ variant, size, className }))}
        ref={ref}
        {...props}
      />
    );
  }
);
Button.displayName = "Button";

export { Button, buttonVariants };

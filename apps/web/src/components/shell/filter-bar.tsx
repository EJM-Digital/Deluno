import * as React from "react";
import { cn } from "../../lib/utils";

interface FilterBarProps {
  primary?: React.ReactNode;
  chips?: React.ReactNode;
  trailing?: React.ReactNode;
  sticky?: boolean;
  className?: string;
}

export function FilterBar({
  primary,
  chips,
  trailing,
  sticky = true,
  className
}: FilterBarProps) {
  return (
    <div
      className={cn(
        sticky &&
          "sticky top-[var(--topbar-height-mobile)] z-20 -mx-4 bg-background/85 px-4 backdrop-blur supports-[backdrop-filter]:bg-background/70 lg:top-[var(--topbar-height)] md:-mx-6 md:px-6",
        "flex flex-col gap-2 border-b border-hairline py-2 md:flex-row md:items-center md:gap-3 md:py-2.5",
        className
      )}
    >
      {primary ? <div className="min-w-0 flex-1">{primary}</div> : null}
      {chips ? (
        <div className="flex min-w-0 items-center gap-1.5 overflow-x-auto no-scrollbar md:flex-wrap md:overflow-visible">
          {chips}
        </div>
      ) : null}
      {trailing ? (
        <div className="flex items-center gap-2 md:ml-auto">{trailing}</div>
      ) : null}
    </div>
  );
}

export function FilterChip({
  active,
  children,
  onClick,
  icon: Icon,
  className
}: {
  active?: boolean;
  children: React.ReactNode;
  onClick?: () => void;
  icon?: React.ComponentType<{ className?: string }>;
  className?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "inline-flex min-h-[var(--control-height-sm)] shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full border px-3 text-[length:var(--type-caption)] font-medium transition-colors",
        active
          ? "border-primary/30 bg-primary/10 text-primary"
          : "border-hairline bg-surface-1 text-muted-foreground hover:bg-surface-2 hover:text-foreground",
        className
      )}
    >
      {Icon ? <Icon className="h-3.5 w-3.5" /> : null}
      {children}
    </button>
  );
}

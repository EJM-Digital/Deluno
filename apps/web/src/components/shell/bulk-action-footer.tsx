import * as React from "react";
import { X } from "lucide-react";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";

interface BulkActionFooterProps {
  count: number;
  onClear?: () => void;
  label?: string;
  actions?: React.ReactNode;
  className?: string;
}

export function BulkActionFooter({
  count,
  onClear,
  label = "selected",
  actions,
  className
}: BulkActionFooterProps) {
  if (count <= 0) return null;

  return (
    <div
      role="region"
      aria-label="Bulk actions"
      className={cn(
        "pointer-events-auto fixed inset-x-0 bottom-[calc(var(--mobile-tabbar-height)+env(safe-area-inset-bottom))] z-40 flex justify-center px-3 pb-3",
        "md:bottom-4 md:inset-x-auto md:left-1/2 md:-translate-x-1/2 md:px-0",
        className
      )}
    >
      <div
        className={cn(
          "flex w-full max-w-3xl items-center gap-2 rounded-2xl border border-hairline bg-card-elevated/95 p-2 pl-3 shadow-lg backdrop-blur",
          "md:w-auto md:min-w-[420px] animate-slide-up"
        )}
      >
        <div className="flex items-center gap-2 pr-1">
          <span className="tabular inline-flex h-6 min-w-[1.5rem] items-center justify-center rounded-md bg-primary px-1.5 text-xs font-semibold text-primary-foreground">
            {count}
          </span>
          <span className="text-sm text-muted-foreground">{label}</span>
        </div>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          {actions}
          {onClear ? (
            <Button
              type="button"
              size="sm"
              variant="ghost"
              onClick={onClear}
              aria-label="Clear selection"
              className="tap-target h-9 w-9 p-0"
            >
              <X className="h-4 w-4" />
            </Button>
          ) : null}
        </div>
      </div>
    </div>
  );
}

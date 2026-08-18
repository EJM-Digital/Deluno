import * as React from "react";
import { ChevronDown } from "lucide-react";
import { cn } from "../../lib/utils";

interface DisclosureProps {
  title: React.ReactNode;
  /** One line describing what's inside, shown while collapsed. */
  summary?: React.ReactNode;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  children: React.ReactNode;
  className?: string;
}

/**
 * "Fine-tune" — the one way to hide advanced fields. A 52px row that expands
 * in place. Keep the beginner path clean; put everything optional behind one.
 */
export function Disclosure({ title, summary, open, onOpenChange, children, className }: DisclosureProps) {
  const id = React.useId();
  return (
    <div className={cn("overflow-hidden rounded-[10px] border border-hairline bg-surface-2/50", className)}>
      <button
        type="button"
        aria-expanded={open}
        aria-controls={id}
        onClick={() => onOpenChange(!open)}
        className="flex min-h-[52px] w-full items-center justify-between gap-[var(--grid-gap)] px-[var(--field-pad-x)] py-2 text-left transition-colors hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        <span className="min-w-0">
          <span className="block text-[length:var(--type-body-sm)] font-medium text-foreground">{title}</span>
          {summary ? (
            <span className="mt-0.5 block truncate text-[length:var(--type-caption)] text-muted-foreground">{summary}</span>
          ) : null}
        </span>
        <ChevronDown
          aria-hidden
          className={cn("h-4 w-4 shrink-0 text-muted-foreground transition-transform duration-150", open && "rotate-180")}
        />
      </button>
      {open ? (
        <div id={id} className="grid gap-[var(--grid-gap)] border-t border-hairline bg-card p-[var(--field-pad-x)]">
          {children}
        </div>
      ) : null}
    </div>
  );
}

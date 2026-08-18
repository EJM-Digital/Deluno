import * as React from "react";
import { NavLink } from "react-router-dom";
import { cn } from "../../lib/utils";

export interface ToolbarTab {
  to: string;
  label: React.ReactNode;
  end?: boolean;
}

interface PageToolbarProps {
  /** Sub-pages of this area. Rendered as a segmented tab bar on the left. */
  tabs?: readonly ToolbarTab[];
  /**
   * The same slot as `tabs`, for a page whose sections are local state rather
   * than routes — a detail page switching between Episodes and History has no
   * URL per section, but the control still belongs where tabs live.
   * Ignored when `tabs` is set.
   */
  left?: React.ReactNode;
  /** At most two: one primary ("New …") and one secondary. */
  actions?: React.ReactNode;
  className?: string;
}

/**
 * The first row of every collection page: 40px, tabs left, actions right.
 * The topbar already names the page, so there is no H1 here.
 */
export function PageToolbar({ tabs, left, actions, className }: PageToolbarProps) {
  return (
    <div className={cn("flex min-h-[var(--toolbar-height)] items-center justify-between gap-[var(--grid-gap)]", className)}>
      {tabs?.length ? (
        <nav
          aria-label="Sections"
          className="no-scrollbar flex h-[var(--control-height)] max-w-full items-center gap-0.5 overflow-x-auto rounded-[10px] border border-hairline bg-surface-2 p-[3px]"
        >
          {tabs.map((tab) => (
            <NavLink
              key={tab.to}
              to={tab.to}
              end={tab.end}
              className={({ isActive }) =>
                cn(
                  "flex h-full shrink-0 items-center rounded-[8px] px-3 text-[length:var(--type-body-sm)] font-medium transition-colors",
                  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                  isActive ? "bg-card font-semibold text-foreground shadow-card" : "text-muted-foreground hover:bg-card/50 hover:text-foreground"
                )
              }
            >
              {tab.label}
            </NavLink>
          ))}
        </nav>
      ) : left ? (
        <div className="flex min-w-0 items-center gap-2">{left}</div>
      ) : (
        <span />
      )}
      {actions ? <div className="flex shrink-0 items-center gap-2">{actions}</div> : null}
    </div>
  );
}

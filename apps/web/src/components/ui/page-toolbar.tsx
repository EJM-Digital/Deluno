import * as React from "react";
import { Plus } from "lucide-react";
import { NavLink, useLocation } from "react-router-dom";
import { findConfigurationArea } from "../../lib/configuration-areas";
import { titleCaseLabel } from "../../lib/title-case";
import { cn } from "../../lib/utils";
import { HowThisWorks } from "../app/how-this-works";
import { Button, type ButtonProps } from "./button";

export interface ToolbarTab {
  to: string;
  label: React.ReactNode;
  end?: boolean;
  icon?: React.ReactNode;
  /** A verified completion state for setup-style rails. Never inferred from navigation alone. */
  status?: "complete" | "pending";
}

interface PageToolbarProps {
  /** Sub-pages of this area. Rendered as a consistent navigation rail. */
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

export interface PageToolbarActionProps extends Omit<ButtonProps, "children"> {
  children: React.ReactNode;
}

/** The shared primary action treatment for collection-page toolbar rails. */
export function PageToolbarAction({ children, className, ...props }: PageToolbarActionProps) {
  const { style, variant, ...buttonProps } = props;
  const actionStyle = (variant ?? "default") === "default"
    ? {
        ...style,
        backgroundImage: "linear-gradient(to bottom, var(--toolbar-accent), var(--toolbar-accent))",
        color: "hsl(var(--background))"
      }
    : style;

  return (
    <Button
      type="button"
      size="sm"
      variant={variant}
      style={actionStyle}
      className={cn("shrink-0 px-3.5", className)}
      {...buttonProps}
    >
      <Plus aria-hidden="true" className="h-3.5 w-3.5 shrink-0" />
      {children}
    </Button>
  );
}

/**
 * The first row of every collection page: 40px, section rail left, actions right.
 * The topbar already names the page, so there is no H1 here.
 *
 * It also carries the area's "How this works" explainer, directly under the tab
 * strip it describes. That lives here rather than in each page because the
 * explainer belongs to the area, not to whichever of its tabs you happened to
 * open — and seven pages each remembering to render one is seven chances to
 * forget, which is how Find & Download ended up without one (#296).
 */
export function PageToolbar({ tabs, left, actions, className }: PageToolbarProps) {
  const location = useLocation();
  // Every tab of a configuration area opens with the same explainer, because
  // what it explains is how those tabs relate — which is exactly what you
  // cannot see from any one of them.
  const area = findConfigurationArea(location.pathname);
  const accentStyle = {
    "--toolbar-accent": "hsl(var(--primary))"
  } as React.CSSProperties;

  // A fragment, not a wrapper: the toolbar and the explainer become siblings in
  // the page's own layout, so the gap between them is the page's `--page-gap`
  // like every other gap, rather than a margin this component invents.
  return (
    <>
      <div className={cn("flex h-[var(--toolbar-height)] min-h-[var(--toolbar-height)] items-center justify-between gap-[var(--grid-gap)] border-b border-hairline/80 dark:border-white/[0.08]", className)} style={accentStyle}>
        {tabs?.length ? (
          <nav
            aria-label="Sections"
            className="no-scrollbar flex h-[var(--toolbar-height)] min-w-0 flex-1 items-stretch overflow-x-auto bg-transparent"
          >
            <div className="flex h-full min-w-max items-stretch gap-[var(--grid-gap)]">
              {tabs.map((tab) => {
                const isActive = isTabActive(location.pathname, tab);
                const isComplete = tab.status === "complete";
                const tabLabel = typeof tab.label === "string" ? titleCaseLabel(tab.label) : undefined;

                return (
                  <NavLink
                    key={tab.to}
                    to={tab.to}
                    end={tab.end}
                    aria-label={tabLabel ? `${tabLabel}${isComplete ? " — complete" : ""}` : undefined}
                    data-status={isComplete ? "complete" : "pending"}
                    className={cn(
                      "group relative flex h-full shrink-0 items-center pt-px text-[length:var(--type-body-sm)] font-medium leading-none transition-colors duration-200",
                      "focus-visible:z-10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                      isActive
                        ? "font-semibold text-[var(--toolbar-accent)] after:absolute after:inset-x-0 after:bottom-2.5 after:h-0.5 after:bg-[var(--toolbar-accent)]"
                        : "text-muted-foreground hover:text-foreground"
                    )}
                  >
                    <span className="whitespace-nowrap">{tabLabel ?? tab.label}</span>
                  </NavLink>
                );
              })}
            </div>
          </nav>
        ) : left ? (
          <div className="flex min-w-0 items-center gap-2">{left}</div>
        ) : (
          <span />
        )}
        {actions ? <div className="flex shrink-0 items-center justify-end gap-2">{actions}</div> : null}
      </div>
      {area ? <HowThisWorks id={area.id} lead={area.explainer.lead} steps={area.explainer.steps} /> : null}
    </>
  );
}

function isTabActive(pathname: string, tab: ToolbarTab) {
  return tab.end ? pathname === tab.to : pathname === tab.to || pathname.startsWith(`${tab.to}/`);
}

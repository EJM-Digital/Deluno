import * as React from "react";
import { Plus } from "lucide-react";
import { NavLink, useLocation } from "react-router-dom";
import { findConfigurationArea } from "../../lib/configuration-areas";
import { titleCaseLabel } from "../../lib/title-case";
import { cn } from "../../lib/utils";
import { HowThisWorks } from "../app/how-this-works";
import { Button, type ButtonProps } from "./button";
import { SegmentedControl } from "./segmented-control";
import { HeaderTabs, HeaderTabsPortal, useHeaderSlot, type HeaderTabItem } from "./header-tabs";

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
  /**
   * A switch between sections of one page whose state is local (Day | Week | Month, Movies | TV): not routes, so not
   * `tabs`, but the same thing to the person, so they are tabs on the header's title line from `lg` up and a segmented
   * control in this row below it. A value picker (a filter, a view) is not this: it stays a control in `left`/`actions`.
   * Ignored when `tabs` is set.
   */
  sections?: {
    label: string;
    value: string;
    onValueChange: (value: string) => void;
    options: readonly { value: string; label: string }[];
  };
  /** At most two: one primary ("New …") and one secondary. */
  actions?: React.ReactNode;
  className?: string;
}

/** The least room the title line keeps for tabs when a page's buttons join it: the chosen tab, a gap and "More". */
const HEADER_TABS_MIN_ROOM = 200;

export interface PageToolbarActionProps extends Omit<ButtonProps, "children"> {
  children: React.ReactNode;
}

/** The shared primary action treatment for collection-page toolbar rails. */
export function PageToolbarAction({ children, className, ...props }: PageToolbarActionProps) {
  const { style, variant, ...buttonProps } = props;
  const actionStyle = (variant ?? "default") === "default"
    ? {
        ...style,
        // The fallback is for the header: a page's buttons sit there from `lg` up, outside the row that sets the accent.
        backgroundImage: "linear-gradient(to bottom, var(--toolbar-accent, hsl(var(--primary))), var(--toolbar-accent, hsl(var(--primary))))",
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
export function PageToolbar({ tabs, left, sections, actions, className }: PageToolbarProps) {
  const location = useLocation();
  // Every tab of a configuration area opens with the same explainer, because
  // what it explains is how those tabs relate — which is exactly what you
  // cannot see from any one of them.
  const area = findConfigurationArea(location.pathname);
  const accentStyle = {
    "--toolbar-accent": "hsl(var(--primary))"
  } as React.CSSProperties;

  const slot = useHeaderSlot();
  const actionSlot = useHeaderSlot("topbar-page-actions");
  const rowActionsRef = React.useRef<HTMLDivElement>(null);
  const [actionsFit, setActionsFit] = React.useState(true);
  const headerItems: HeaderTabItem[] = (tabs ?? []).map((tab) => {
    const isComplete = tab.status === "complete";
    const tabLabel = typeof tab.label === "string" ? titleCaseLabel(tab.label) : undefined;
    return {
      key: tab.to,
      label: tabLabel ?? (typeof tab.label === "string" ? tab.label : tab.to),
      selected: isTabActive(location.pathname, tab),
      to: tab.to,
      end: tab.end,
      ariaLabel: tabLabel ? `${tabLabel}${isComplete ? " — complete" : ""}` : undefined,
      status: isComplete ? "complete" : "pending"
    };
  });
  const hasTabs = Boolean(tabs?.length);
  const hasSections = !hasTabs && Boolean(sections?.options.length);
  // With the header's slot present the tabs sit on its title line from `lg` up (the owner, 2 Oct 2026), so this row keeps
  // only what the page itself puts in it; a page with tabs and no buttons has no row there. Below `lg`, and on a page
  // rendered without the header, the tabs stay in this row as a scrolling strip.
  const tabsInHeader = (hasTabs || hasSections) && slot !== null;
  // The page's buttons go on the title line too, right of the tabs and left of search (the owner, 2 Oct 2026), so a page
  // whose row held only tabs and buttons has no row at all from `lg` up.
  // Only while they fit beside the title, the chosen tab and More: a title page's three buttons at 1280 squeezed its
  // title and tabs to nothing. When they do not, they stay in this row, as below `lg`.
  const actionsInHeader = Boolean(actions) && actionSlot !== null && actionsFit;
  const wantsTabRoom = hasTabs || hasSections;
  React.useLayoutEffect(() => {
    if (!actions || !actionSlot || typeof ResizeObserver === "undefined") return undefined;
    const main = document.getElementById("topbar-main");
    const title = document.getElementById("topbar-title");
    if (!main || !title) return undefined;
    const check = () => {
      const placed = actionSlot.childElementCount ? actionSlot : rowActionsRef.current;
      const buttons = placed?.getBoundingClientRect().width ?? 0;
      const gap = parseFloat(getComputedStyle(main).columnGap) || 0;
      const need = title.getBoundingClientRect().width + (wantsTabRoom ? HEADER_TABS_MIN_ROOM : 0) + gap + buttons;
      setActionsFit(need <= main.getBoundingClientRect().width);
    };
    check();
    const observer = new ResizeObserver(check);
    observer.observe(main);
    observer.observe(actionSlot);
    if (rowActionsRef.current) observer.observe(rowActionsRef.current);
    return () => observer.disconnect();
  }, [actions, actionSlot, actionsInHeader, wantsTabRoom]);
  const rowHasLeft = !tabsInHeader && (hasTabs || hasSections || Boolean(left));
  const rowEmpty = !rowHasLeft && (!actions || actionsInHeader);
  const sectionItems: HeaderTabItem[] = (hasSections ? sections!.options : []).map((option) => ({
    key: option.value,
    label: option.label,
    selected: option.value === sections!.value,
    onSelect: () => sections!.onValueChange(option.value)
  }));

  // A fragment, not a wrapper: the toolbar and the explainer become siblings in
  // the page's own layout, so the gap between them is the page's `--page-gap`
  // like every other gap, rather than a margin this component invents.
  return (
    <>
      {tabsInHeader ? (
        <HeaderTabsPortal slot={slot}>
          {hasSections ? <HeaderTabs role="tablist" items={sectionItems} label={sections!.label} /> : <HeaderTabs items={headerItems} label="Sections" />}
        </HeaderTabsPortal>
      ) : null}
      {actionsInHeader ? <HeaderTabsPortal slot={actionSlot}>{actions}</HeaderTabsPortal> : null}
      <div className={cn("flex min-h-(--toolbar-height) min-w-0 max-w-full flex-wrap items-center justify-between gap-(--grid-gap) border-b border-hairline/80 dark:border-white/8 sm:h-(--toolbar-height) sm:flex-nowrap", rowEmpty && "hidden", className)} style={accentStyle}>
        {hasTabs && !tabsInHeader ? (
          <nav
            aria-label="Sections"
            className="no-scrollbar flex h-(--toolbar-height) min-w-0 flex-1 items-stretch overflow-x-auto bg-transparent"
          >
            <div className="flex h-full min-w-max items-stretch gap-[18px]">
              {(tabs ?? []).map((tab) => {
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
                    /*
                      The one tab style in Deluno (the owner, 2 Oct 2026), the same as the Dashboard's Live and System: plain
                      14px text 18px apart, the chosen one in the accent colour with a 2px underline at the row's bottom
                      line. The weight never changes between tabs, so the row does not shift as you move along it.
                    */
                    className={cn(
                      "group relative flex h-full shrink-0 items-center text-(length:--type-body-sm) font-medium leading-none transition-colors duration-200",
                      "focus-visible:z-10 focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring",
                      isActive
                        ? "text-(--toolbar-accent)"
                        : "text-muted-foreground hover:text-foreground"
                    )}
                  >
                    <span
                      aria-hidden
                      className={cn(
                        "absolute inset-x-0 bottom-0 h-[2px] rounded-full transition-colors",
                        isActive ? "bg-(--toolbar-accent)" : "bg-transparent"
                      )}
                    />
                    <span className="whitespace-nowrap">{tabLabel ?? tab.label}</span>
                  </NavLink>
                );
              })}
            </div>
          </nav>
        ) : hasTabs || tabsInHeader ? (
          <span />
        ) : hasSections ? (
          <SegmentedControl
            aria-label={sections!.label}
            className="w-auto"
            value={sections!.value}
            onValueChange={sections!.onValueChange}
            options={[...sections!.options]}
          />
        ) : left ? (
          <div className="flex min-w-0 items-center gap-2">{left}</div>
        ) : (
          <span />
        )}
        {actions && !actionsInHeader ? <div ref={rowActionsRef} className="flex max-w-full shrink-0 flex-wrap items-center justify-end gap-2">{actions}</div> : null}
      </div>
      {area ? <HowThisWorks id={area.id} lead={area.explainer.lead} steps={area.explainer.steps} /> : null}
    </>
  );
}

function isTabActive(pathname: string, tab: ToolbarTab) {
  return tab.end ? pathname === tab.to : pathname === tab.to || pathname.startsWith(`${tab.to}/`);
}

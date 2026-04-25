import { NavLink } from "react-router-dom";
import { cn } from "../../lib/utils";
import { AttentionBadge, type Severity } from "./attention-dot";

export interface SectionTabItem {
  to: string;
  label: string;
  end?: boolean;
  count?: number;
  severity?: Severity;
}

interface SectionTabsProps {
  items: SectionTabItem[];
  className?: string;
}

export function SectionTabs({ items, className }: SectionTabsProps) {
  return (
    <nav
      aria-label="Section navigation"
      className={cn(
        "relative -mx-4 border-b border-hairline px-4 md:-mx-6 md:px-6",
        className
      )}
    >
      <div className="flex gap-1 overflow-x-auto no-scrollbar scroll-fade-x">
        {items.map((item) => (
          <NavLink
            key={item.to}
            to={item.to}
            end={item.end}
            className={({ isActive }) =>
              cn(
                "relative inline-flex shrink-0 items-center gap-2 whitespace-nowrap px-3 py-2.5 text-sm font-medium transition-colors",
                "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded-t-md",
                isActive
                  ? "text-foreground after:absolute after:inset-x-0 after:-bottom-px after:h-0.5 after:bg-primary after:rounded-full"
                  : "text-muted-foreground hover:text-foreground"
              )
            }
          >
            <span>{item.label}</span>
            {typeof item.count === "number" && item.count > 0 ? (
              <AttentionBadge count={item.count} severity={item.severity} />
            ) : null}
          </NavLink>
        ))}
      </div>
    </nav>
  );
}

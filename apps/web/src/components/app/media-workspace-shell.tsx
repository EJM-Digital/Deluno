import { NavLink, Outlet } from "react-router-dom";
import { cn } from "../../lib/utils";

/** Tab definitions for the Movies workspace */
export const moviesWorkspaceTabs = [
  { to: "/movies", label: "Library", end: true, tip: "Browse, filter, and manage your movie collection" },
  { to: "/movies/wanted", label: "Wanted", end: false, tip: "Movies that are missing or ready for a quality upgrade" },
  { to: "/movies/upgrades", label: "Upgrades", end: false, tip: "Movies already in your library that Deluno wants to improve" },
  { to: "/movies/import", label: "Needs attention", end: false, tip: "Import failures and unresolved handoffs that need your review" }
] as const;

/** Tab definitions for the TV Shows workspace */
export const tvWorkspaceTabs = [
  { to: "/tv", label: "Library", end: true, tip: "Browse, filter, and manage your TV show collection" },
  { to: "/tv/wanted", label: "Wanted", end: false, tip: "Episodes and seasons that are missing or ready for an upgrade" },
  { to: "/tv/upgrades", label: "Upgrades", end: false, tip: "TV content already in your library that Deluno wants to improve" },
  { to: "/tv/import", label: "Needs attention", end: false, tip: "Import failures and unresolved handoffs that need your review" }
] as const;

type Tab = { to: string; label: string; end: boolean; tip: string };

function MediaTabRail({ tabs }: { tabs: readonly Tab[] }) {
  return (
    <div className="no-scrollbar overflow-x-auto">
      <nav
        role="tablist"
        aria-label="Media workspace sections"
        className="flex min-w-max items-center gap-1 rounded-2xl border border-hairline/80 bg-card/85 p-1.5 shadow-card dark:border-white/[0.07] dark:bg-white/[0.035]"
      >
        {tabs.map((tab) => (
          <NavLink
            key={tab.to}
            to={tab.to}
            end={tab.end}
            role="tab"
            title={tab.tip}
            className={({ isActive }) =>
              cn(
                "relative flex items-center whitespace-nowrap rounded-xl border border-transparent px-4 py-2 text-[13px] font-semibold transition-all duration-200",
                "text-muted-foreground hover:bg-surface-1 hover:text-foreground",
                isActive &&
                  "border-primary/30 bg-primary/12 text-foreground shadow-[inset_0_0_0_1px_hsl(var(--primary)/0.08)]"
              )
            }
          >
            {({ isActive }) => (
              <>
                {isActive ? (
                  <span
                    aria-hidden
                    className="absolute left-0 top-1/2 h-5 w-[3px] -translate-y-1/2 rounded-full bg-primary"
                  />
                ) : null}
                <span className="pl-1">{tab.label}</span>
              </>
            )}
          </NavLink>
        ))}
      </nav>
    </div>
  );
}

/** Layout shell for the /movies/* workspace — renders tabs + <Outlet /> */
export function MoviesWorkspaceLayout() {
  return (
    <div className="space-y-[var(--page-gap)]">
      <MediaTabRail tabs={moviesWorkspaceTabs} />
      <Outlet />
    </div>
  );
}

/** Layout shell for the /tv/* workspace — renders tabs + <Outlet /> */
export function TvWorkspaceLayout() {
  return (
    <div className="space-y-[var(--page-gap)]">
      <MediaTabRail tabs={tvWorkspaceTabs} />
      <Outlet />
    </div>
  );
}

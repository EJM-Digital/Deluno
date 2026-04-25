import { Fragment } from "react";
import {
  Activity,
  Calendar,
  Download,
  Film,
  LayoutGrid,
  LogOut,
  MoreHorizontal,
  RadioTower,
  ServerCog,
  Settings,
  Tv
} from "lucide-react";
import { NavLink, useLocation } from "react-router-dom";
import { Sheet, SheetClose, SheetContent, SheetTrigger } from "../ui/sheet";
import { AttentionDot, type Severity } from "./attention-dot";
import { cn } from "../../lib/utils";
import type { AttentionSnapshot } from "../../lib/use-attention";

const PRIMARY = [
  { to: "/", label: "Home", icon: LayoutGrid, end: true as const },
  { to: "/movies", label: "Movies", icon: Film, end: false as const },
  { to: "/tv", label: "TV", icon: Tv, end: false as const },
  { to: "/queue", label: "Queue", icon: Download, end: false as const }
] as const;

const DRAWER_LINKS = [
  { to: "/calendar", label: "Calendar", icon: Calendar, group: "Library" as const },
  { to: "/indexers", label: "Indexers", icon: RadioTower, group: "Library" as const },
  { to: "/activity", label: "Activity", icon: Activity, group: "App" as const },
  { to: "/system", label: "System", icon: ServerCog, group: "App" as const },
  { to: "/settings", label: "Settings", icon: Settings, group: "App" as const }
] as const;

function moreTabActive(pathname: string): boolean {
  if (pathname.startsWith("/calendar")) return true;
  if (pathname.startsWith("/indexers")) return true;
  if (pathname.startsWith("/activity")) return true;
  if (pathname.startsWith("/system")) return true;
  if (pathname.startsWith("/settings")) return true;
  return false;
}

function indexerAttention(attention: AttentionSnapshot): Severity | undefined {
  if (attention.indexerAlerts > 0) return "warn";
  return undefined;
}

export interface MobileShellNavProps {
  attention: AttentionSnapshot;
  user: { displayName: string; username: string; avatarInitials?: string } | null;
  onLogout: () => void;
}

/**
 * Mobile-only primary chrome: bottom tab rail + “More” bottom sheet.
 * Tabs are four main destinations; everything else opens in a short, centered drawer.
 */
export function MobileShellNav({ attention, user, onLogout }: MobileShellNavProps) {
  const { pathname } = useLocation();
  const moreActive = moreTabActive(pathname);
  const indexerAttn = indexerAttention(attention);

  const primaryWithAttention = PRIMARY.map((tab) => {
    if (tab.to === "/queue" && attention.failedJobs > 0) {
      return { ...tab, attention: "danger" as const };
    }
    return { ...tab };
  });

  return (
    <Sheet>
      <Fragment>
      <nav
        aria-label="Primary navigation"
        className={cn(
          "fixed inset-x-0 bottom-0 z-[45] border-t border-hairline/80 bg-card/98 backdrop-blur-xl",
          "supports-[backdrop-filter]:bg-card/90 pb-[env(safe-area-inset-bottom)] lg:hidden",
          "shadow-[0_-8px_32px_hsl(0_0%_0%/0.1)] dark:shadow-[0_-8px_36px_hsl(0_0%_0%/0.4)]"
        )}
        style={{ height: "calc(var(--mobile-tabbar-height) + env(safe-area-inset-bottom))" }}
      >
        <ul className="mx-auto grid h-[var(--mobile-tabbar-height)] w-full max-w-md grid-cols-5">
          {primaryWithAttention.map((tab) => {
            const Icon = tab.icon;
            return (
              <li key={tab.to} className="flex min-w-0">
                <NavLink
                  to={tab.to}
                  end={tab.end}
                  className={({ isActive }) =>
                    cn(
                      "relative flex min-w-0 w-full flex-col items-center justify-center gap-0.5 px-0.5",
                      "text-[length:var(--shell-subtle-size)] font-semibold leading-tight tracking-tight text-muted-foreground transition-colors",
                      "active:bg-muted/40 tap-target",
                      isActive && "text-foreground"
                    )
                  }
                >
                  {({ isActive }) => (
                    <>
                      <span className="relative flex h-[var(--control-height-icon)] w-[var(--control-height-icon)] shrink-0 items-center justify-center rounded-xl transition-colors">
                        <Icon
                          className={cn("h-[calc(var(--shell-icon-size)+0.35rem)] w-[calc(var(--shell-icon-size)+0.35rem)]", isActive ? "text-primary" : "text-muted-foreground")}
                          strokeWidth={isActive ? 2.1 : 1.75}
                        />
                        {"attention" in tab && tab.attention ? (
                          <AttentionDot
                            severity={tab.attention}
                            className="absolute right-0 top-0.5"
                            pulse={tab.attention === "danger"}
                          />
                        ) : null}
                      </span>
                      <span className="max-w-full truncate px-0.5">{tab.label}</span>
                      {isActive ? (
                        <span
                          aria-hidden
                          className="absolute left-1/2 top-1 h-0.5 w-6 -translate-x-1/2 rounded-full bg-primary"
                        />
                      ) : null}
                    </>
                  )}
                </NavLink>
              </li>
            );
          })}
          <li className="flex min-w-0">
            <SheetTrigger asChild>
              <button
                type="button"
                className={cn(
                  "relative flex min-w-0 w-full flex-col items-center justify-center gap-0.5 px-0.5",
                  "text-[length:var(--shell-subtle-size)] font-semibold leading-tight tracking-tight transition-colors tap-target",
                  "text-muted-foreground active:bg-muted/40",
                  moreActive && "text-foreground"
                )}
                aria-label="More destinations"
              >
                <span className="relative flex h-[var(--control-height-icon)] w-[var(--control-height-icon)] shrink-0 items-center justify-center rounded-xl">
                  <MoreHorizontal
                    className={cn("h-[calc(var(--shell-icon-size)+0.35rem)] w-[calc(var(--shell-icon-size)+0.35rem)]", moreActive ? "text-primary" : "text-muted-foreground")}
                    strokeWidth={moreActive ? 2.1 : 1.75}
                  />
                  {indexerAttn ? (
                    <AttentionDot severity={indexerAttn} className="absolute right-0 top-0.5" />
                  ) : null}
                </span>
                <span className="max-w-full truncate px-0.5">More</span>
                {moreActive ? (
                  <span
                    aria-hidden
                    className="absolute left-1/2 top-1 h-0.5 w-6 -translate-x-1/2 rounded-full bg-primary"
                  />
                ) : null}
              </button>
            </SheetTrigger>
          </li>
        </ul>
      </nav>

      <SheetContent side="bottom" className="flex max-h-[min(88dvh,640px)] flex-col gap-0 p-0">
        <div className="flex shrink-0 flex-col items-center border-b border-hairline/70 px-4 pb-2 pt-3 dark:border-white/[0.06]">
          <div
            aria-hidden
            className="mb-3 h-1 w-10 shrink-0 rounded-full bg-muted-foreground/25"
          />
          <div className="flex w-full items-start justify-between gap-3 pr-10">
            <div className="min-w-0">
              <p className="text-dynamic-base font-semibold tracking-tight text-foreground">Navigate</p>
              <p className="text-[length:var(--shell-subtle-size)] text-muted-foreground">Calendar, indexers, and app pages</p>
            </div>
          </div>
        </div>

        <nav
          aria-label="Additional destinations"
          className="min-h-0 flex-1 overflow-y-auto overscroll-y-contain px-2 py-2"
        >
          {(["Library", "App"] as const).map((group) => (
            <div key={group} className="mb-3 last:mb-0">
              <p className="px-3 pb-1.5 pt-2 text-[length:var(--shell-subtle-size)] font-semibold uppercase tracking-wider text-muted-foreground/70">
                {group}
              </p>
              <ul className="space-y-0.5">
                {DRAWER_LINKS.filter((l) => l.group === group).map((item) => {
                  const Icon = item.icon;
                  const isActive = pathname === item.to || pathname.startsWith(`${item.to}/`);
                  return (
                    <li key={item.to}>
                      <SheetClose asChild>
                        <NavLink
                          to={item.to}
                          className={cn(
                            "flex items-center gap-3 rounded-xl px-3 py-2.5 text-dynamic-base font-medium transition-colors",
                            isActive
                              ? "bg-primary/12 text-foreground ring-1 ring-inset ring-primary/20"
                              : "text-muted-foreground hover:bg-muted/60 hover:text-foreground"
                          )}
                        >
                          <Icon
                            className={cn("h-5 w-5 shrink-0", isActive ? "text-primary" : "text-muted-foreground")}
                            strokeWidth={isActive ? 2.1 : 1.75}
                          />
                          <span className="flex-1">{item.label}</span>
                          {item.to === "/indexers" && indexerAttn ? (
                            <AttentionDot severity={indexerAttn} className="shrink-0" />
                          ) : null}
                        </NavLink>
                      </SheetClose>
                    </li>
                  );
                })}
              </ul>
            </div>
          ))}
        </nav>

        <div className="shrink-0 border-t border-hairline/70 bg-muted/15 px-3 py-3 dark:border-white/[0.06]">
          {user ? (
            <div className="mb-2 flex items-center gap-3 rounded-xl px-2 py-1.5">
              <div className="flex h-[var(--control-height-icon)] w-[var(--control-height-icon)] shrink-0 items-center justify-center rounded-full bg-gradient-to-br from-primary to-[hsl(var(--primary-2))] text-[length:var(--shell-subtle-size)] font-bold text-primary-foreground">
                {user.avatarInitials ?? "DU"}
              </div>
              <div className="min-w-0 flex-1">
                <p className="truncate text-dynamic-base font-semibold text-foreground">{user.displayName}</p>
                <p className="truncate text-[length:var(--shell-subtle-size)] text-muted-foreground">@{user.username}</p>
              </div>
            </div>
          ) : null}
          <SheetClose asChild>
            <button
              type="button"
              onClick={onLogout}
              className="flex w-full items-center gap-2 rounded-xl px-3 py-2.5 text-left text-dynamic-base font-medium text-muted-foreground transition-colors hover:bg-destructive/10 hover:text-destructive"
            >
              <LogOut className="h-4 w-4 shrink-0" />
              Sign out
            </button>
          </SheetClose>
        </div>
      </SheetContent>
      </Fragment>
    </Sheet>
  );
}

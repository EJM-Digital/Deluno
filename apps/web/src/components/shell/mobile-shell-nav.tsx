import { Fragment, useEffect, useState } from "react";
import { ChevronRight, LogOut, MoreHorizontal } from "lucide-react";
import { NavLink, useLocation } from "react-router-dom";
import { Sheet, SheetClose, SheetContent, SheetTrigger } from "../ui/sheet";
import { AttentionDot, type Severity } from "./attention-dot";
import { cn } from "../../lib/utils";
import type { AttentionSnapshot } from "../../lib/use-attention";
import { configurationNavAreas, librarySetupNavItems, maintenanceNavItems } from "../app/settings-shell";
import { DelunoNavGlyph, type DelunoNavGlyphKind } from "./deluno-nav-glyph";

const PRIMARY = [
  { to: "/", label: "Dashboard", icon: "dashboard", end: true as const },
  { to: "/movies", label: "Movies", icon: "movies", end: false as const },
  { to: "/tv", label: "TV", icon: "shows", end: false as const },
  { to: "/queue", label: "Transfers", icon: "transfers", end: false as const }
] as const;

const DRAWER_LINKS = [
  { to: "/calendar", label: "Schedule", icon: "schedule", group: "Your media" as const },
  { to: "/search-cycles", label: "Automation", icon: "automation", group: "What Deluno is doing" as const },
  { to: "/activity", label: "Activity", icon: "activity", group: "What Deluno is doing" as const }
] as const;

function moreTabActive(pathname: string): boolean {
  if (pathname.startsWith("/calendar")) return true;
  if (pathname.startsWith("/indexers")) return true;
  if (pathname.startsWith("/search-cycles")) return true;
  if (pathname.startsWith("/activity")) return true;
  if (pathname.startsWith("/system")) return true;
  if (pathname.startsWith("/setup-guide")) return true;
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
                        <DelunoNavGlyph
                          kind={tab.icon}
                          className={cn("h-[calc(var(--shell-icon-size)+0.35rem)] w-[calc(var(--shell-icon-size)+0.35rem)]", isActive ? "text-primary" : "text-muted-foreground")}
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
            </div>
          </div>
        </div>

        <nav
          aria-label="Additional destinations"
          className="min-h-0 flex-1 overflow-y-auto overscroll-y-contain px-2 py-2"
        >
          {(["Your media", "What Deluno is doing", "Set up your library", "Maintain Deluno"] as const).map((group) => (
            <div key={group} className="mb-3 last:mb-0">
              <p className="px-3 pb-1.5 pt-2 text-[length:var(--shell-subtle-size)] font-semibold uppercase tracking-wider text-muted-foreground/70">
                {group}
              </p>
              <ul className="space-y-0.5">
                {group === "Set up your library" ? <MobileConfigurationTree pathname={pathname} /> : null}
                {group === "Maintain Deluno" ? <MobileMaintenanceTree pathname={pathname} /> : null}
                {DRAWER_LINKS.filter((l) => l.group === group).map((item) => {
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
                          <DelunoNavGlyph
                            kind={item.icon as DelunoNavGlyphKind}
                            className={cn("h-5 w-5 shrink-0", isActive ? "text-primary" : "text-muted-foreground")}
                          />
                          <span className="flex-1">{item.label}</span>
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

function MobileConfigurationTree({ pathname }: { pathname: string }) {
  const activeArea = configurationNavAreas.find((area) => area.match(pathname));
  const isLibrarySetupRoute = pathname === "/settings" || librarySetupNavItems.some((item) => pathname.startsWith(item.to));
  const [setupOpen, setSetupOpen] = useState(isLibrarySetupRoute);
  const [openAreas, setOpenAreas] = useState<Set<string>>(() => new Set(activeArea ? [activeArea.label] : []));

  useEffect(() => {
    if (isLibrarySetupRoute) setSetupOpen(true);
    if (activeArea) setOpenAreas((current) => new Set([...current, activeArea.label]));
  }, [activeArea?.label, isLibrarySetupRoute]);

  const toggleArea = (label: string) => {
    setOpenAreas((current) => {
      const next = new Set(current);
      if (next.has(label)) next.delete(label);
      else next.add(label);
      return next;
    });
  };

  return (
    <li className="space-y-1.5">
      <div className="flex min-h-11 items-center gap-1 rounded-xl">
        <SheetClose asChild>
          <NavLink
            to="/settings"
            end
            className={cn(
              "flex min-w-0 flex-1 items-center gap-3 rounded-xl px-3 text-dynamic-base font-bold transition-colors",
              isLibrarySetupRoute ? "bg-primary/12 text-foreground ring-1 ring-inset ring-primary/20" : "text-muted-foreground hover:bg-muted/60 hover:text-foreground"
            )}
          >
              <DelunoNavGlyph kind="setup" className={cn("h-5 w-5 shrink-0", isLibrarySetupRoute ? "text-primary" : "text-muted-foreground")} />
            <span className="flex-1">Library setup</span>
          </NavLink>
        </SheetClose>
        <button
          type="button"
          aria-label={`${setupOpen ? "Collapse" : "Expand"} Library setup`}
          aria-expanded={setupOpen}
          onClick={() => setSetupOpen((open) => !open)}
          className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl text-muted-foreground transition hover:bg-muted/60 hover:text-foreground"
        >
          <ChevronRight className={cn("h-4 w-4 transition-transform", setupOpen && "rotate-90 text-primary")} />
        </button>
      </div>
      {setupOpen ? (
        <div className="ml-7 mt-0.5 space-y-1 pl-3">
          {librarySetupNavItems.map((item) => (
            <SheetClose asChild key={item.to}>
              <NavLink to={item.to} end={item.end} className={({ isActive }) => cn("relative block rounded-lg px-3 py-1.5 text-[13px] font-medium transition before:absolute before:left-0 before:top-1/2 before:h-1 before:w-1 before:-translate-y-1/2 before:rounded-full", isActive ? "bg-primary/10 text-primary before:bg-primary" : "text-muted-foreground before:bg-muted-foreground/35 hover:bg-muted/60 hover:text-foreground")}>
                {item.label}
              </NavLink>
            </SheetClose>
          ))}
        </div>
      ) : null}

      {configurationNavAreas.map((area) => {
        const hasChildren = area.items.some((item) => item.to !== area.to);
        const isOpen = openAreas.has(area.label);
        const isActive = activeArea?.label === area.label;
        return (
          <div key={area.label}>
            <div className="flex min-h-11 items-center gap-1 rounded-xl">
              <SheetClose asChild>
                <NavLink to={area.to} className={cn("flex min-w-0 flex-1 items-center gap-3 rounded-xl px-3 text-dynamic-base font-bold transition-colors", isActive ? "bg-primary/12 text-foreground ring-1 ring-inset ring-primary/20" : "text-muted-foreground hover:bg-muted/60 hover:text-foreground")}>
                  <span className={cn("flex h-7 w-7 shrink-0 items-center justify-center rounded-lg", isActive ? "bg-primary/18 text-primary" : "bg-muted/35 text-muted-foreground")}><DelunoNavGlyph kind={area.icon} className="h-4 w-4" /></span>
                  <span className="min-w-0 truncate">{area.label}</span>
                </NavLink>
              </SheetClose>
              {hasChildren ? <button type="button" aria-label={`${isOpen ? "Collapse" : "Expand"} ${area.label}`} aria-expanded={isOpen} onClick={() => toggleArea(area.label)} className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl text-muted-foreground transition hover:bg-muted/60 hover:text-foreground"><ChevronRight className={cn("h-4 w-4 transition-transform", isOpen && "rotate-90 text-primary")} /></button> : null}
            </div>
            {isOpen && hasChildren ? <div className="ml-7 mt-0.5 space-y-1 pl-3">{area.items.filter((item) => item.to !== area.to).map((item) => <SheetClose asChild key={item.to}><NavLink to={item.to} end={item.end} className={({ isActive: routeIsActive }) => cn("relative block rounded-lg px-3 py-1.5 text-[13px] font-medium transition before:absolute before:left-0 before:top-1/2 before:h-1 before:w-1 before:-translate-y-1/2 before:rounded-full", routeIsActive ? "bg-primary/10 text-primary before:bg-primary" : "text-muted-foreground before:bg-muted-foreground/35 hover:bg-muted/60 hover:text-foreground")}>{item.label}</NavLink></SheetClose>)}</div> : null}
          </div>
        );
      })}
    </li>
  );
}

function MobileMaintenanceTree({ pathname }: { pathname: string }) {
  const activeArea = maintenanceNavItems.find((area) => area.match(pathname));
  const isMaintenanceRoute = Boolean(activeArea);
  const [open, setOpen] = useState(isMaintenanceRoute);

  useEffect(() => {
    if (isMaintenanceRoute) setOpen(true);
  }, [isMaintenanceRoute]);

  const area = maintenanceNavItems[0];
  return (
    <li>
      <div className="flex min-h-11 items-center gap-1 rounded-xl">
        <SheetClose asChild>
          <NavLink
            to={area.to}
            className={cn(
              "flex min-w-0 flex-1 items-center gap-3 rounded-xl px-3 text-dynamic-base font-bold transition-colors",
              isMaintenanceRoute ? "bg-primary/12 text-foreground ring-1 ring-inset ring-primary/20" : "text-muted-foreground hover:bg-muted/60 hover:text-foreground"
            )}
          >
            <DelunoNavGlyph kind="system" className={cn("h-5 w-5 shrink-0", isMaintenanceRoute ? "text-primary" : "text-muted-foreground")} />
            <span className="flex-1">System &amp; settings</span>
          </NavLink>
        </SheetClose>
        <button
          type="button"
          aria-label={`${open ? "Collapse" : "Expand"} System & settings`}
          aria-expanded={open}
          onClick={() => setOpen((current) => !current)}
          className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl text-muted-foreground transition hover:bg-muted/60 hover:text-foreground"
        >
          <ChevronRight className={cn("h-4 w-4 transition-transform", open && "rotate-90 text-primary")} />
        </button>
      </div>
      {open ? (
        <div className="ml-7 mt-0.5 space-y-1 pl-3">
          {area.items.filter((item) => item.to !== area.to).map((item) => (
            <SheetClose asChild key={item.to}>
              <NavLink
                to={item.to}
                end={item.end}
                className={({ isActive }) => cn("relative block rounded-lg px-3 py-1.5 text-[13px] font-medium transition before:absolute before:left-0 before:top-1/2 before:h-1 before:w-1 before:-translate-y-1/2 before:rounded-full", isActive ? "bg-primary/10 text-primary before:bg-primary" : "text-muted-foreground before:bg-muted-foreground/35 hover:bg-muted/60 hover:text-foreground")}
              >
                {item.label}
              </NavLink>
            </SheetClose>
          ))}
        </div>
      ) : null}
    </li>
  );
}

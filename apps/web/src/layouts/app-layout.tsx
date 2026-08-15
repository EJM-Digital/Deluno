import {
  Bell,
  ChevronDown,
  ChevronRight,
  HelpCircle,
  LoaderCircle,
  LogOut,
  LockKeyhole,
  Moon,
  Search,
  SunMedium,
} from "lucide-react";
import { useTheme } from "next-themes";
import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useHotkeys } from "react-hotkeys-hook";
import { NavLink, useLocation, useNavigate, useNavigation } from "react-router-dom";
import { CommandPalette } from "../components/shell/command-palette";
import { KeyboardHintOverlay } from "../components/shell/keyboard-hint-overlay";
import { MobileShellNav } from "../components/shell/mobile-shell-nav";
import { PageTransition } from "../components/shell/motion";
import { RouteSkeleton } from "../components/shell/skeleton";
import { Toaster } from "../components/shell/toaster";
import { WsStatusBadge } from "../components/shell/ws-status-badge";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { globalShortcuts } from "../lib/command-registry";
import { useAttention } from "../lib/use-attention";
import { useAuth, type UserProfile } from "../lib/use-auth";
import { DENSITY_LABELS, DensityProvider, useDensity, type Density } from "../lib/use-density";
import { SignalRProvider } from "../lib/use-signalr";
import { cn } from "../lib/utils";
import { configurationNavAreas, maintenanceNavItems } from "../components/app/settings-shell";
import { DelunoNavGlyph, type DelunoNavGlyphKind } from "../components/shell/deluno-nav-glyph";

function isEditableTarget(target: EventTarget | null) {
  if (!(target instanceof HTMLElement)) return false;
  const tag = target.tagName;
  if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return true;
  return target.isContentEditable;
}

const libraryNav = [
  { to: "/", label: "Dashboard", icon: "dashboard", end: true, attention: "none" as const },
  { to: "/movies", label: "Movies", icon: "movies", end: false, attention: "movies" as const },
  { to: "/tv", label: "TV Shows", icon: "shows", end: false, attention: "tv" as const },
  { to: "/calendar", label: "Schedule", icon: "schedule", end: false, attention: "none" as const }
] as const;

const operationsNav = [
  { to: "/queue", label: "Transfers", icon: "transfers", end: false, attention: "activity" as const },
  { to: "/search-cycles", label: "Automation", icon: "automation", end: false, attention: "none" as const },
  { to: "/activity", label: "Activity", icon: "activity", end: false, attention: "activity" as const }
] as const;

const densityChoices: Density[] = ["compact", "comfortable", "spacious", "expanded"];

const routeMeta = [
  { match: (path: string) => path === "/", title: "Dashboard", subtitle: "Your movies and shows, in one place" },
  { match: (path: string) => path.startsWith("/movies"), title: "Movies", subtitle: "Manage and grow your movie library" },
  { match: (path: string) => path.startsWith("/tv"), title: "TV Shows", subtitle: "Manage your shows, episodes, and upgrades" },
  { match: (path: string) => path.startsWith("/calendar"), title: "Schedule", subtitle: "Upcoming releases and retry windows" },
  { match: (path: string) => path.startsWith("/queue"), title: "Transfers", subtitle: "Follow downloads through processing and safe import" },
  { match: (path: string) => path.startsWith("/indexers"), title: "Connections", subtitle: "Search sources and download apps Deluno uses" },
  { match: (path: string) => path.startsWith("/search-cycles"), title: "Automation", subtitle: "Choose what Deluno should search for, retry, and upgrade next" },
  { match: (path: string) => path.startsWith("/activity"), title: "Activity", subtitle: "The permanent record of what happened and why" },
  { match: (path: string) => path.startsWith("/settings"), title: "Library setup", subtitle: "How Deluno manages your media" },
  { match: (path: string) => path.startsWith("/system"), title: "System", subtitle: "Health, backups, updates, and audit" }
];

export function AppLayout() {
  return <AppLayoutInner />;
}

function AppLayoutInner() {
  const { token } = useAuth();
  return (
    <SignalRProvider accessToken={token}>
      <AppLayoutContent />
    </SignalRProvider>
  );
}

function AppLayoutContent() {
  const location = useLocation();
  const navigate = useNavigate();
  const navigation = useNavigation();
  const { user, logout } = useAuth();
  const { resolvedTheme, setTheme } = useTheme();
  const attention = useAttention();
  const [commandOpen, setCommandOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);

  const meta = useMemo(
    () => routeMeta.find((item) => item.match(location.pathname)) ?? routeMeta[0],
    [location.pathname]
  );

  useHotkeys("mod+k", (e) => { e.preventDefault(); setCommandOpen(true); }, { enableOnFormTags: true }, []);
  useHotkeys("shift+/", (e) => { if (isEditableTarget(e.target)) return; e.preventDefault(); setHelpOpen(true); }, [], []);
  useHotkeys("/", (e) => { if (isEditableTarget(e.target)) return; e.preventDefault(); setSearchOpen(true); }, [], []);

  useEffect(() => {
    let armed = false;
    let timer: number;
    const go: Record<string, string> = { o: "/", m: "/movies", t: "/tv", q: "/queue", i: "/indexers", x: "/search-cycles", a: "/activity", c: "/calendar", s: "/settings", y: "/system" };
    function onKeyDown(e: KeyboardEvent) {
      if (e.metaKey || e.ctrlKey || e.altKey || isEditableTarget(e.target)) { armed = false; return; }
      if (armed) {
        window.clearTimeout(timer); armed = false;
        const path = go[e.key.toLowerCase()];
        if (path) { e.preventDefault(); navigate(path); }
        return;
      }
      if (e.key === "g" || e.key === "G") { armed = true; timer = window.setTimeout(() => { armed = false; }, 1000); }
    }
    window.addEventListener("keydown", onKeyDown);
    return () => { window.removeEventListener("keydown", onKeyDown); window.clearTimeout(timer); };
  }, [navigate]);

  return (
    <DensityProvider>
      <div className="relative min-h-dvh overflow-x-hidden bg-background text-foreground">
        <a
          href="#main-content"
          className={cn(
            "sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-3 focus:z-[100]",
            "focus:inline-flex focus:items-center focus:gap-2 focus:rounded-xl",
            "focus:border focus:border-primary/50 focus:bg-background/95 focus:px-3 focus:py-2",
            "focus:text-sm focus:font-semibold focus:text-foreground focus:shadow-lg",
            "focus:outline-none focus-visible:ring-2 focus-visible:ring-primary"
          )}
        >
          Skip to content
        </a>

        <div aria-hidden className="pointer-events-none fixed inset-0 -z-10 overflow-hidden">
          <div className="absolute -top-60 left-[38%] h-[680px] w-[680px] rounded-full bg-primary/[0.08] blur-[190px]" />
          <div className="absolute bottom-0 right-0 h-[520px] w-[520px] rounded-full bg-[hsl(var(--primary-2))]/[0.055] blur-[170px]" />
        </div>

        <CommandPalette open={commandOpen} onOpenChange={setCommandOpen} theme={resolvedTheme} onToggleTheme={() => setTheme(resolvedTheme === "dark" ? "light" : "dark")} />
        <KeyboardHintOverlay open={helpOpen} onOpenChange={setHelpOpen} shortcuts={globalShortcuts.map((s) => ({ keys: s.keys, label: s.label, group: s.group }))} />

        <div className="min-h-dvh">
          <DesktopSidebar attention={attention} user={user} onLogout={logout} />

          <div className="min-w-0 pb-mobile-tabbar lg:ml-[var(--sidebar-width)] lg:pb-0">
            <ContentTopbar
              title={meta.title}
              subtitle={meta.subtitle}
              attention={attention}
              resolvedTheme={resolvedTheme}
              setTheme={setTheme}
              onOpenCommand={() => setCommandOpen(true)}
              onOpenHelp={() => setHelpOpen(true)}
              searchOpen={searchOpen}
              setSearchOpen={setSearchOpen}
              user={user}
              onLogout={logout}
            />

            <main
              id="main-content"
              className="w-full min-w-0"
              style={{
                paddingInline: "var(--content-pad-inline)",
                paddingTop: "var(--content-pad-block)",
                paddingBottom: "var(--content-pad-block)"
              }}
            >
              {navigation.state === "idle" ? <PageTransition /> : <RouteSkeleton />}
            </main>
          </div>
        </div>

        <MobileShellNav attention={attention} user={user} onLogout={logout} />
        <Toaster />
      </div>
    </DensityProvider>
  );
}

function DesktopSidebar({
  attention,
  user,
  onLogout
}: {
  attention: ReturnType<typeof useAttention>;
  user: UserProfile | null;
  onLogout: () => void;
}) {
  const { changePassword } = useAuth();
  const { pathname } = useLocation();
  const [passwordOpen, setPasswordOpen] = useState(false);
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [passwordBusy, setPasswordBusy] = useState(false);
  const [passwordMessage, setPasswordMessage] = useState<string | null>(null);

  async function handleChangePassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPasswordMessage(null);

    if (newPassword !== confirmPassword) {
      setPasswordMessage("New passwords do not match.");
      return;
    }

    setPasswordBusy(true);
    try {
      await changePassword(currentPassword, newPassword);
      setPasswordMessage("Password changed.");
      setCurrentPassword("");
      setNewPassword("");
      setConfirmPassword("");
      window.setTimeout(() => {
        setPasswordOpen(false);
        setPasswordMessage(null);
      }, 700);
    } catch (error) {
      setPasswordMessage(error instanceof Error ? error.message : "Password could not be changed.");
    } finally {
      setPasswordBusy(false);
    }
  }

  return (
    <aside className="fixed inset-y-0 left-0 z-50 hidden w-[var(--sidebar-width)] border-r border-hairline/80 bg-sidebar/95 px-[calc(var(--tile-pad)*0.8)] py-[calc(var(--tile-pad)*1.15)] lg:flex lg:flex-col">
      <NavLink to="/" aria-label="Deluno home" className="flex min-h-[calc(var(--shell-pill-height)*1.8)] items-center gap-3 rounded-2xl border border-hairline/80 bg-card/75 px-[calc(var(--tile-pad)*0.65)] text-foreground shadow-card no-underline dark:border-white/[0.07] dark:bg-white/[0.035]">
        <AppMark size={42} />
        <span className="min-w-0">
          <span className="block whitespace-nowrap font-display text-[length:var(--shell-brand-size)] font-bold tracking-[-0.04em]">Deluno</span>
          <span className="block whitespace-nowrap text-[length:var(--shell-subtle-size)] font-medium text-muted-foreground">Media Manager</span>
        </span>
      </NavLink>

      <div className="mt-5 min-h-0 flex-1 overflow-x-hidden overflow-y-auto pr-1 [scrollbar-width:none] [&::-webkit-scrollbar]:hidden">
        <div className="mb-2 px-[calc(var(--shell-nav-pad-x)*0.55)] text-[length:var(--shell-subtle-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
          Your media
        </div>
        <nav aria-label="Media dashboard" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
          {libraryNav.map((item) => (
            <SidebarItem
              key={item.to}
              item={item}
              count={attentionCount(attention, item.attention)}
            />
          ))}
        </nav>

        <div className="my-5 h-px bg-hairline/80" />

        <div className="mb-2 px-[calc(var(--shell-nav-pad-x)*0.55)] text-[length:var(--shell-subtle-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
          What Deluno is doing
        </div>
        <nav aria-label="Automation and transfer status" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
          {operationsNav.map((item) => (
            <SidebarItem
              key={item.to}
              item={item}
              count={attentionCount(attention, item.attention)}
            />
          ))}
        </nav>

        <div className="my-5 h-px bg-hairline/80" />

        <div className="mb-2 px-[calc(var(--shell-nav-pad-x)*0.55)] text-[length:var(--shell-subtle-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
          Set up your library
        </div>
        <nav aria-label="Library setup" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
          <ConfigurationSidebarTree pathname={pathname} />
        </nav>

        <div className="my-5 h-px bg-hairline/80" />

        <div className="mb-2 px-[calc(var(--shell-nav-pad-x)*0.55)] text-[length:var(--shell-subtle-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
          Maintain Deluno
        </div>
        <nav aria-label="System controls" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
          <MaintenanceSidebarTree pathname={pathname} />
        </nav>
      </div>

      <div className="mt-3 rounded-2xl border border-hairline/80 bg-card/75 p-[calc(var(--tile-pad)*0.8)] shadow-card dark:border-white/[0.07] dark:bg-white/[0.035]">
        <div className="flex items-center gap-2">
          <span className="h-2 w-2 rounded-full bg-success shadow-[0_0_12px_hsl(var(--success)/0.8)]" />
          <span className="density-nowrap text-[length:var(--type-body-sm)] font-semibold text-foreground">All systems normal</span>
        </div>
        <p className="mt-2 text-[length:var(--shell-subtle-size)] text-muted-foreground">
          {attention.failedJobs > 0 ? `${attention.failedJobs} failed job${attention.failedJobs !== 1 ? "s" : ""}` : "No active issues"}
        </p>
      </div>

      <div className="group relative z-50 mt-3">
        <button
          type="button"
          className="flex min-h-[var(--shell-pill-height)] w-full items-center gap-3 rounded-2xl border border-hairline/80 bg-card/75 px-[calc(var(--tile-pad)*0.65)] text-left transition hover:border-primary/30 hover:bg-muted/30 dark:border-white/[0.07] dark:bg-white/[0.035]"
        >
          <span className="flex h-[var(--shell-avatar-size)] w-[var(--shell-avatar-size)] shrink-0 items-center justify-center rounded-full bg-gradient-to-br from-primary to-[hsl(var(--primary-2))] text-[length:var(--type-body-sm)] font-bold text-primary-foreground">
            {user?.avatarInitials ?? "DU"}
          </span>
          <span className="min-w-0 flex-1">
            <span className="block whitespace-nowrap text-[length:var(--type-body-sm)] font-semibold text-foreground">{user?.displayName ?? "User"}</span>
            <span className="block whitespace-nowrap text-[length:var(--type-caption)] text-muted-foreground">@{user?.username ?? "deluno"}</span>
          </span>
          <ChevronDown className="h-4 w-4 text-muted-foreground" />
        </button>
        <div className="absolute bottom-0 left-[calc(100%+12px)] z-[90] w-64 overflow-hidden rounded-xl border border-hairline bg-card/95 opacity-0 shadow-lg backdrop-blur-xl transition group-focus-within:opacity-100 group-hover:opacity-100 dark:border-white/[0.07]">
          <button
            type="button"
            onClick={() => setPasswordOpen(true)}
            className="flex w-full items-center gap-2 px-3 py-2.5 text-sm font-medium text-muted-foreground transition hover:bg-secondary hover:text-foreground"
          >
            <LockKeyhole className="h-4 w-4" />
            Change password
          </button>
          <button
            type="button"
            onClick={onLogout}
            className="flex w-full items-center gap-2 px-3 py-2.5 text-sm font-medium text-muted-foreground transition hover:bg-destructive/10 hover:text-destructive"
          >
            <LogOut className="h-4 w-4" />
            Sign out
          </button>
        </div>
      </div>
      {passwordOpen ? (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-background/65 p-4 backdrop-blur-sm">
          <form
            onSubmit={(event) => void handleChangePassword(event)}
            className="w-full max-w-md rounded-2xl border border-hairline bg-card p-5 shadow-lg dark:border-white/[0.07]"
          >
            <div className="flex items-start justify-between gap-[var(--grid-gap)]">
              <div>
                <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground">
                  Account
                </p>
                <h2 className="mt-2 font-display text-2xl font-semibold tracking-tight text-foreground">
                  Change password
                </h2>
                <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
                  Update the password for {user?.displayName ?? "this user"}.
                </p>
              </div>
              <button
                type="button"
                onClick={() => setPasswordOpen(false)}
                className="rounded-xl px-3 py-2 text-sm font-semibold text-muted-foreground transition hover:bg-secondary hover:text-foreground"
              >
                Close
              </button>
            </div>

            <div className="mt-5 space-y-3">
              <label className="block">
                <span className="density-label uppercase tracking-[0.18em] text-muted-foreground">Current password</span>
                <Input
                  className="mt-2"
                  type="password"
                  autoComplete="current-password"
                  value={currentPassword}
                  onChange={(event) => setCurrentPassword(event.target.value)}
                />
              </label>
              <label className="block">
                <span className="density-label uppercase tracking-[0.18em] text-muted-foreground">New password</span>
                <Input
                  className="mt-2"
                  type="password"
                  autoComplete="new-password"
                  value={newPassword}
                  onChange={(event) => setNewPassword(event.target.value)}
                />
              </label>
              <label className="block">
                <span className="density-label uppercase tracking-[0.18em] text-muted-foreground">Confirm new password</span>
                <Input
                  className="mt-2"
                  type="password"
                  autoComplete="new-password"
                  value={confirmPassword}
                  onChange={(event) => setConfirmPassword(event.target.value)}
                />
              </label>
            </div>

            {passwordMessage ? (
              <p className="mt-4 rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-sm text-muted-foreground">
                {passwordMessage}
              </p>
            ) : null}

            <div className="mt-5 flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => setPasswordOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" disabled={passwordBusy || !currentPassword || !newPassword || !confirmPassword}>
                {passwordBusy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <LockKeyhole className="h-4 w-4" />}
                Save password
              </Button>
            </div>
          </form>
        </div>
      ) : null}
    </aside>
  );
}

function ConfigurationSidebarTree({ pathname }: { pathname: string }) {
  const activeArea = configurationNavAreas.find((area) => area.match(pathname));
  const isConfigurationRoute = pathname.startsWith("/settings") || pathname.startsWith("/indexers");
  const [setupOpen, setSetupOpen] = useState(isConfigurationRoute);
  const [openAreas, setOpenAreas] = useState<Set<string>>(() => new Set(activeArea ? [activeArea.label] : []));

  useEffect(() => {
    if (!isConfigurationRoute) return;
    setSetupOpen(true);
    if (activeArea) {
      setOpenAreas((current) => new Set([...current, activeArea.label]));
    }
  }, [activeArea?.label, isConfigurationRoute]);

  const toggleArea = (label: string) => {
    setOpenAreas((current) => {
      const next = new Set(current);
      if (next.has(label)) next.delete(label);
      else next.add(label);
      return next;
    });
  };

  return (
    <div>
      <div className="group relative flex min-h-[var(--shell-pill-height)] items-center rounded-2xl px-[calc(var(--shell-nav-pad-x)*0.55)] text-[length:var(--shell-nav-size)] font-semibold transition-all duration-200">
        <NavLink
          to="/settings"
          className={({ isActive }) => cn(
            "absolute inset-y-0 left-0 right-10 flex min-w-0 items-center gap-3 rounded-l-2xl pl-[calc(var(--shell-nav-pad-x)*0.55)] transition",
            isActive || isConfigurationRoute ? "text-foreground" : "text-muted-foreground hover:text-foreground"
          )}
        >
          <span className="flex h-[calc(var(--shell-pill-height)*0.68)] w-[calc(var(--shell-pill-height)*0.68)] shrink-0 items-center justify-center rounded-xl bg-muted/30 text-muted-foreground">
            <DelunoNavGlyph kind="setup" className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" />
          </span>
          <span className="min-w-0 flex-1 whitespace-nowrap">Library setup</span>
        </NavLink>
        <button
          type="button"
          aria-label={`${setupOpen ? "Collapse" : "Expand"} Library setup`}
          aria-expanded={setupOpen}
          onClick={() => setSetupOpen((open) => !open)}
          className="absolute inset-y-0 right-0 flex w-10 items-center justify-center rounded-r-2xl text-muted-foreground transition hover:bg-muted/50 hover:text-foreground"
        >
          <ChevronRight className={cn("h-4 w-4 transition-transform duration-200", setupOpen && "rotate-90 text-primary")} />
        </button>
      </div>

      {setupOpen ? (
        <div className="ml-[calc((var(--shell-nav-pad-x)*0.55)_+_1rem)] mt-1 space-y-1 pl-4">
          {configurationNavAreas.map((area) => {
            const hasChildren = area.items.some((item) => item.to !== area.to);
            const isOpen = openAreas.has(area.label);
            const isActive = activeArea?.label === area.label;
            return (
              <div key={area.label}>
                <div className="relative flex min-h-8 items-center rounded-lg pr-8">
                  <NavLink
                    to={area.to}
                    className={({ isActive: routeIsActive }) => cn(
                      "flex min-w-0 flex-1 items-center rounded-xl px-2.5 py-2 text-[length:var(--shell-nav-size)] font-semibold transition",
                      routeIsActive || isActive ? "bg-primary/10 text-foreground" : "text-muted-foreground hover:bg-muted/40 hover:text-foreground"
                    )}
                  >
                    {area.label}
                  </NavLink>
                  {hasChildren ? (
                    <button
                      type="button"
                      aria-label={`${isOpen ? "Collapse" : "Expand"} ${area.label}`}
                      aria-expanded={isOpen}
                      onClick={() => toggleArea(area.label)}
                      className="absolute inset-y-0 right-0 flex w-8 items-center justify-center rounded-xl text-muted-foreground transition hover:bg-muted/50 hover:text-foreground"
                    >
                      <ChevronRight className={cn("h-3.5 w-3.5 transition-transform duration-200", isOpen && "rotate-90 text-primary")} />
                    </button>
                  ) : null}
                </div>
                {isOpen && hasChildren ? (
                  <div className="ml-4 mt-1 space-y-1 pl-3">
                    {area.items.filter((item) => item.to !== area.to).map((item) => (
                      <NavLink
                        key={item.to}
                        to={item.to}
                        end={item.end}
                        className={({ isActive: routeIsActive }) => cn(
                          "relative block rounded-lg px-3 py-2 text-[length:var(--shell-nav-size)] font-medium transition before:absolute before:left-0 before:top-1/2 before:h-1 before:w-1 before:-translate-y-1/2 before:rounded-full",
                          routeIsActive ? "bg-primary/10 text-primary before:bg-primary" : "text-muted-foreground before:bg-muted-foreground/35 hover:bg-muted/40 hover:text-foreground"
                        )}
                      >
                        {item.label}
                      </NavLink>
                    ))}
                  </div>
                ) : null}
              </div>
            );
          })}
        </div>
      ) : null}
    </div>
  );
}

function MaintenanceSidebarTree({ pathname }: { pathname: string }) {
  const activeArea = maintenanceNavItems.find((area) => area.match(pathname));
  const isMaintenanceRoute = Boolean(activeArea);
  const [open, setOpen] = useState(isMaintenanceRoute);

  useEffect(() => {
    if (isMaintenanceRoute) setOpen(true);
  }, [isMaintenanceRoute]);

  const area = maintenanceNavItems[0];
  return (
    <div>
      <div className="group relative flex min-h-[var(--shell-pill-height)] items-center rounded-2xl px-[calc(var(--shell-nav-pad-x)*0.55)] text-[length:var(--shell-nav-size)] font-semibold transition-all duration-200">
        <NavLink
          to={area.to}
          className={({ isActive }) => cn(
            "absolute inset-y-0 left-0 right-10 flex min-w-0 items-center gap-3 rounded-l-2xl pl-[calc(var(--shell-nav-pad-x)*0.55)] transition",
            isActive || isMaintenanceRoute ? "text-foreground" : "text-muted-foreground hover:text-foreground"
          )}
        >
          <span className={cn("flex h-[calc(var(--shell-pill-height)*0.68)] w-[calc(var(--shell-pill-height)*0.68)] shrink-0 items-center justify-center rounded-xl", isMaintenanceRoute ? "bg-primary/18 text-primary" : "bg-muted/30 text-muted-foreground")}>
            <DelunoNavGlyph kind="system" className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" />
          </span>
          <span className="min-w-0 flex-1 whitespace-nowrap">System &amp; settings</span>
        </NavLink>
        <button
          type="button"
          aria-label={`${open ? "Collapse" : "Expand"} System & settings`}
          aria-expanded={open}
          onClick={() => setOpen((current) => !current)}
          className="absolute inset-y-0 right-0 flex w-10 items-center justify-center rounded-r-2xl text-muted-foreground transition hover:bg-muted/50 hover:text-foreground"
        >
          <ChevronRight className={cn("h-4 w-4 transition-transform duration-200", open && "rotate-90 text-primary")} />
        </button>
      </div>
      {open ? (
        <div className="ml-[calc((var(--shell-nav-pad-x)*0.55)_+_1rem)] mt-1 space-y-1 pl-4">
          {area.items.filter((item) => item.to !== area.to).map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) => cn(
                "relative block rounded-lg px-3 py-2 text-[length:var(--shell-nav-size)] font-medium transition before:absolute before:left-0 before:top-1/2 before:h-1 before:w-1 before:-translate-y-1/2 before:rounded-full",
                isActive ? "bg-primary/10 text-primary before:bg-primary" : "text-muted-foreground before:bg-muted-foreground/35 hover:bg-muted/40 hover:text-foreground"
              )}
            >
              {item.label}
            </NavLink>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function SidebarItem({
  item,
  count
}: {
  item: { to: string; label: string; icon: DelunoNavGlyphKind; end: boolean };
  count: number;
}) {
  return (
    <NavLink
      to={item.to}
      end={item.end}
      className={({ isActive }) =>
        cn(
          "group relative flex min-h-[var(--shell-pill-height)] items-center gap-3 rounded-2xl px-[calc(var(--shell-nav-pad-x)*0.55)] text-[length:var(--shell-nav-size)] font-semibold transition-all duration-200",
          isActive
            ? "bg-primary/14 text-foreground shadow-[inset_0_0_0_1px_hsl(var(--primary)/0.18)]"
            : "text-muted-foreground hover:bg-muted/40 hover:text-foreground"
        )
      }
    >
      {({ isActive }) => (
        <>
          <span className={cn("absolute left-0 h-[calc(var(--shell-pill-height)*0.58)] w-[3px] rounded-full", isActive ? "bg-primary" : "bg-transparent")} />
          <span className={cn("flex h-[calc(var(--shell-pill-height)*0.68)] w-[calc(var(--shell-pill-height)*0.68)] shrink-0 items-center justify-center rounded-xl transition", isActive ? "bg-primary/18 text-primary" : "bg-muted/30 text-muted-foreground group-hover:text-foreground")}>
            <DelunoNavGlyph kind={item.icon} className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" />
          </span>
          <span className="min-w-0 flex-1 whitespace-nowrap">{item.label}</span>
          {count > 0 ? (
            <span className={cn("flex h-[calc(var(--shell-pill-height)*0.42)] min-w-[calc(var(--shell-pill-height)*0.42)] shrink-0 items-center justify-center rounded-full px-1.5 font-mono text-[length:var(--shell-nav-badge-size)] font-bold", isActive ? "bg-primary text-primary-foreground" : "bg-surface-2 text-muted-foreground")}>
              {count}
            </span>
          ) : null}
        </>
      )}
    </NavLink>
  );
}

function ContentTopbar({
  title,
  subtitle,
  attention,
  resolvedTheme,
  setTheme,
  onOpenCommand,
  onOpenHelp,
  searchOpen,
  setSearchOpen,
  user,
  onLogout
}: {
  title: string;
  subtitle: string;
  attention: ReturnType<typeof useAttention>;
  resolvedTheme?: string;
  setTheme: (t: string) => void;
  onOpenCommand: () => void;
  onOpenHelp: () => void;
  searchOpen: boolean;
  setSearchOpen: (v: boolean) => void;
  user: UserProfile | null;
  onLogout: () => void;
}) {
  const searchRef = useRef<HTMLInputElement>(null);
  const { density, setDensity } = useDensity();
  const [densityOpen, setDensityOpen] = useState(false);
  const densityMenuRef = useRef<HTMLDivElement>(null);
  useEffect(() => { if (searchOpen) setTimeout(() => searchRef.current?.focus(), 50); }, [searchOpen]);
  useEffect(() => {
    if (!densityOpen) return;
    function onPointerDown(event: PointerEvent) {
      if (!densityMenuRef.current?.contains(event.target as Node)) setDensityOpen(false);
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") setDensityOpen(false);
    }
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [densityOpen]);

  return (
    <header className="sticky top-0 z-40 border-b border-hairline/70 bg-background/88 px-[var(--content-pad-inline)] py-3 backdrop-blur-2xl supports-[backdrop-filter]:bg-background/78 lg:py-3.5 dark:border-white/[0.05]">
      <div className="flex w-full min-w-0 items-center gap-[var(--grid-gap)]">
        <NavLink to="/" aria-label="Deluno home" className="flex shrink-0 items-center no-underline lg:hidden">
          <AppMark />
        </NavLink>

        <div className="min-w-0 flex-1">
          <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.14em] text-muted-foreground">{subtitle}</p>
          <h1 className="mt-0.5 font-display text-[length:var(--type-title-sm)] font-semibold leading-tight tracking-tight text-foreground sm:mt-1 sm:text-[length:var(--type-title-md)]">
            {title}
          </h1>
        </div>

        <button
          type="button"
          onClick={onOpenCommand}
          className="hidden min-h-[var(--shell-pill-height)] items-center gap-2 rounded-2xl border border-hairline/70 bg-card/75 px-4 text-left text-[length:var(--shell-nav-size)] font-medium text-muted-foreground transition hover:border-primary/30 hover:bg-muted/40 hover:text-foreground md:flex"
        >
          <Search className="h-[var(--shell-icon-size-sm)] w-[var(--shell-icon-size-sm)]" />
          <span className="hidden xl:inline">Search...</span>
          <kbd className="hidden rounded border border-hairline bg-background/70 px-1.5 py-0.5 font-mono text-[length:var(--shell-kbd-size)] text-muted-foreground/70 xl:inline">CMD K</kbd>
        </button>

        <Button type="button" variant="ghost" size="icon" onClick={() => setSearchOpen(true)} aria-label="Search" className="md:hidden">
          <Search className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" />
        </Button>

        <Button type="button" variant="ghost" size="icon" className="relative text-muted-foreground hover:text-foreground" aria-label="Notifications">
          <Bell className="h-[var(--shell-icon-size-sm)] w-[var(--shell-icon-size-sm)]" strokeWidth={1.75} />
          {attention.failedJobs > 0 ? (
            <span className="absolute right-1.5 top-1.5 h-[5px] w-[5px] rounded-full bg-destructive shadow-[0_0_0_1.5px_hsl(var(--background)),0_0_6px_hsl(var(--destructive)/0.7)]" />
          ) : null}
        </Button>

        <Button type="button" variant="ghost" size="icon" className="hidden text-muted-foreground hover:text-foreground md:inline-flex" onClick={onOpenHelp} aria-label="Keyboard shortcuts">
          <HelpCircle className="h-[var(--shell-icon-size-sm)] w-[var(--shell-icon-size-sm)]" strokeWidth={1.75} />
        </Button>

        <div ref={densityMenuRef} className="relative hidden min-[920px]:block">
          <button
            type="button"
            onClick={() => setDensityOpen((open) => !open)}
            aria-haspopup="menu"
            aria-expanded={densityOpen}
            className={cn(
              "inline-flex min-h-[var(--control-height-icon)] items-center gap-2 rounded-xl border px-3",
              "bg-card/75 text-[length:var(--shell-nav-size)] font-semibold text-muted-foreground transition",
              "hover:border-primary/30 hover:bg-muted/40 hover:text-foreground",
              densityOpen ? "border-primary/45 text-foreground shadow-[0_0_0_1px_hsl(var(--primary)/0.12),0_10px_30px_hsl(var(--primary)/0.08)]" : "border-hairline/70"
            )}
          >
            <span aria-hidden className="h-1.5 w-1.5 rounded-full bg-primary shadow-[0_0_10px_hsl(var(--primary)/0.65)]" />
            <span>{DENSITY_LABELS[density]}</span>
            <ChevronDown className={cn("h-[var(--shell-icon-size-sm)] w-[var(--shell-icon-size-sm)] transition", densityOpen && "rotate-180")} strokeWidth={1.75} />
          </button>

          {densityOpen ? (
            <div
              role="menu"
              aria-label="Display density"
              className="absolute right-0 top-[calc(100%+8px)] z-[80] w-52 overflow-hidden rounded-xl border border-hairline/80 bg-card/95 p-1.5 shadow-lg backdrop-blur-xl dark:border-white/[0.07]"
            >
              {densityChoices.map((choice) => {
                const selected = choice === density;
                return (
                  <button
                    key={choice}
                    type="button"
                    role="menuitemradio"
                    aria-checked={selected}
                    onClick={() => {
                      setDensity(choice);
                      setDensityOpen(false);
                    }}
                    className={cn(
                      "flex min-h-[var(--control-height-sm)] w-full items-center justify-between gap-3 rounded-lg px-3 text-left",
                      "text-[length:var(--shell-nav-size)] font-semibold transition",
                      selected
                        ? "bg-primary/12 text-foreground ring-1 ring-inset ring-primary/20"
                        : "text-muted-foreground hover:bg-muted/45 hover:text-foreground"
                    )}
                  >
                    <span>{DENSITY_LABELS[choice]}</span>
                    {selected ? <span className="h-1.5 w-1.5 rounded-full bg-primary shadow-[0_0_10px_hsl(var(--primary)/0.75)]" /> : null}
                  </button>
                );
              })}
            </div>
          ) : null}
        </div>

        <button
          type="button"
          onClick={() => setTheme(resolvedTheme === "dark" ? "light" : "dark")}
          className="relative flex h-[var(--control-height-icon)] w-[var(--control-height-icon)] items-center justify-center rounded-xl text-muted-foreground transition hover:bg-muted/50 hover:text-foreground"
          aria-label={resolvedTheme === "dark" ? "Switch to light mode" : "Switch to dark mode"}
        >
          <SunMedium className={cn("absolute h-[var(--shell-icon-size-sm)] w-[var(--shell-icon-size-sm)] transition duration-300", resolvedTheme === "light" ? "scale-100 opacity-100" : "scale-75 opacity-0 -rotate-90")} strokeWidth={1.75} />
          <Moon className={cn("absolute h-[var(--shell-icon-size-sm)] w-[var(--shell-icon-size-sm)] transition duration-300", resolvedTheme === "dark" ? "scale-100 opacity-100" : "scale-75 opacity-0 rotate-90")} strokeWidth={1.75} />
        </button>

        <WsStatusBadge className="hidden xl:inline-flex" />
      </div>

      {searchOpen ? (
        <div className="fixed inset-0 z-50 flex flex-col bg-background p-3 pt-safe lg:hidden">
          <div className="flex items-center gap-2">
            <Input ref={searchRef} autoFocus placeholder="Search..." className="flex-1" />
            <Button type="button" variant="outline" onClick={() => setSearchOpen(false)}>Done</Button>
          </div>
          <Button type="button" className="mt-4" variant="secondary" onClick={() => { setSearchOpen(false); onOpenCommand(); }}>
            Open command palette
          </Button>
        </div>
      ) : null}

      <span className="sr-only">
        Signed in as {user?.displayName ?? "User"}.
        <button type="button" onClick={onLogout}>Sign out</button>
      </span>
    </header>
  );
}

function attentionCount(attention: ReturnType<typeof useAttention>, key: "none" | "movies" | "tv" | "indexers" | "activity") {
  if (key === "movies") return attention.movieWanted;
  if (key === "tv") return attention.tvWanted;
  if (key === "indexers") return attention.indexerAlerts;
  if (key === "activity") return attention.failedJobs;
  return 0;
}

function AppMark({ size = 30 }: { size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 30 30"
      fill="none"
      aria-hidden
      style={{
        filter: "drop-shadow(0 2px 8px hsl(var(--primary-deep)/0.45))",
        flexShrink: 0,
      }}
    >
      <rect width="30" height="30" rx="7.5" fill="url(#mark-bg)" />
      <rect x="0.75" y="0.75" width="28.5" height="14" rx="6.75" fill="white" fillOpacity="0.08" />
      <rect x="4.5" y="7.5" width="21" height="15" rx="2.1" stroke="white" strokeWidth="1.5" fill="none" />
      <rect x="2.7" y="10.8" width="3.6" height="2.7" rx="0.6" fill="white" />
      <rect x="2.7" y="16.5" width="3.6" height="2.7" rx="0.6" fill="white" />
      <rect x="23.7" y="10.8" width="3.6" height="2.7" rx="0.6" fill="white" />
      <rect x="23.7" y="16.5" width="3.6" height="2.7" rx="0.6" fill="white" />
      <polygon points="12.3,12 12.3,18 17.7,15" fill="white" />
      <defs>
        <linearGradient id="mark-bg" x1="0" y1="0" x2="30" y2="30" gradientUnits="userSpaceOnUse">
          <stop style={{ stopColor: "hsl(var(--primary))" }} />
          <stop offset="1" style={{ stopColor: "hsl(var(--primary-2))" }} />
        </linearGradient>
      </defs>
    </svg>
  );
}

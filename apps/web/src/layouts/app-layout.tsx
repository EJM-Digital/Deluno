import {
  Activity,
  Bell,
  Calendar,
  ChevronDown,
  Download,
  Film,
  HelpCircle,
  LayoutGrid,
  LoaderCircle,
  LogOut,
  LockKeyhole,
  Moon,
  RadioTower,
  Search,
  ServerCog,
  Settings,
  SunMedium,
  Tv
} from "lucide-react";
import { useTheme } from "next-themes";
import { useEffect, useMemo, useRef, useState, type ComponentType, type FormEvent } from "react";
import { useHotkeys } from "react-hotkeys-hook";
import { NavLink, useLocation, useNavigate } from "react-router-dom";
import { CommandPalette } from "../components/shell/command-palette";
import { KeyboardHintOverlay } from "../components/shell/keyboard-hint-overlay";
import { MobileShellNav } from "../components/shell/mobile-shell-nav";
import { PageTransition } from "../components/shell/motion";
import { Toaster } from "../components/shell/toaster";
import { WsStatusBadge } from "../components/shell/ws-status-badge";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { globalShortcuts } from "../lib/command-registry";
import { useAttention } from "../lib/use-attention";
import { useAuth, type UserProfile } from "../lib/use-auth";
import { DensityProvider } from "../lib/use-density";
import { SignalRProvider } from "../lib/use-signalr";
import { cn } from "../lib/utils";

function isEditableTarget(target: EventTarget | null) {
  if (!(target instanceof HTMLElement)) return false;
  const tag = target.tagName;
  if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return true;
  return target.isContentEditable;
}

const primaryNav = [
  { to: "/", label: "Overview", icon: LayoutGrid, end: true, attention: "none" as const },
  { to: "/movies", label: "Movies", icon: Film, end: false, attention: "movies" as const },
  { to: "/tv", label: "TV Shows", icon: Tv, end: false, attention: "tv" as const },
  { to: "/calendar", label: "Calendar", icon: Calendar, end: false, attention: "none" as const },
  { to: "/queue", label: "Queue", icon: Download, end: false, attention: "activity" as const },
  { to: "/indexers", label: "Indexers", icon: RadioTower, end: false, attention: "indexers" as const },
  { to: "/activity", label: "Activity", icon: Activity, end: false, attention: "activity" as const }
] as const;

const utilityNav = [
  { to: "/settings", label: "Settings", icon: Settings, end: false },
  { to: "/system", label: "System", icon: ServerCog, end: false }
] as const;

const routeMeta = [
  { match: (path: string) => path === "/", title: "Overview", subtitle: "Unified media operations" },
  { match: (path: string) => path.startsWith("/movies"), title: "Movies", subtitle: "Browse, filter, and route media" },
  { match: (path: string) => path.startsWith("/tv"), title: "TV Shows", subtitle: "Series, episodes, and monitoring" },
  { match: (path: string) => path.startsWith("/calendar"), title: "Calendar", subtitle: "Upcoming releases and retry windows" },
  { match: (path: string) => path.startsWith("/queue"), title: "Queue", subtitle: "Unified download client telemetry" },
  { match: (path: string) => path.startsWith("/indexers"), title: "Indexers", subtitle: "Search sources and provider health" },
  { match: (path: string) => path.startsWith("/activity"), title: "Activity", subtitle: "Events, history, and automation" },
  { match: (path: string) => path.startsWith("/settings"), title: "Settings", subtitle: "Guided setup and configuration" },
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
    const go: Record<string, string> = { o: "/", m: "/movies", t: "/tv", q: "/queue", i: "/indexers", a: "/activity", c: "/calendar", s: "/settings", y: "/system" };
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
              className="mx-auto w-full"
              style={{
                maxWidth: "min(var(--content-max-width), calc(100vw - (var(--content-outer-gap) * 2)))",
                paddingInline: "var(--content-pad-inline)",
                paddingTop: "var(--content-pad-block)",
                paddingBottom: "var(--content-pad-block)"
              }}
            >
              <PageTransition />
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
          <span className="block truncate font-display text-[length:var(--shell-brand-size)] font-bold tracking-[-0.04em]">Deluno</span>
          <span className="block truncate text-[length:var(--shell-subtle-size)] font-medium text-muted-foreground">Media Manager</span>
        </span>
      </NavLink>

      <div className="my-5 h-px bg-hairline/80" />

      <nav aria-label="Primary navigation" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
        {primaryNav.map((item) => (
          <SidebarItem
            key={item.to}
            item={item}
            count={attentionCount(attention, item.attention)}
          />
        ))}
      </nav>

      <div className="my-5 h-px bg-hairline/80" />

      <nav aria-label="Controls" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
        {utilityNav.map((item) => (
          <SidebarItem key={`${item.label}-${item.to}`} item={item} count={0} />
        ))}
      </nav>

      <div className="min-h-6 flex-1" />

      <div className="rounded-2xl border border-hairline/80 bg-card/75 p-[calc(var(--tile-pad)*0.8)] shadow-card dark:border-white/[0.07] dark:bg-white/[0.035]">
        <div className="flex items-center gap-2">
          <span className="h-2 w-2 rounded-full bg-success shadow-[0_0_12px_hsl(var(--success)/0.8)]" />
          <span className="density-nowrap text-[length:var(--type-body-sm)] font-semibold text-foreground">All systems normal</span>
        </div>
        <p className="mt-2 text-[length:var(--shell-subtle-size)] text-muted-foreground">
          4 active downloads
        </p>
        <p className="density-nowrap mt-1 font-mono text-[length:var(--type-body-lg)] font-semibold text-primary">82.3 MB/s</p>
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
            <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{user?.displayName ?? "User"}</span>
            <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">@{user?.username ?? "deluno"}</span>
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
            <div className="flex items-start justify-between gap-4">
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

function SidebarItem({
  item,
  count
}: {
  item: { to: string; label: string; icon: ComponentType<{ className?: string; strokeWidth?: number }>; end: boolean };
  count: number;
}) {
  const Icon = item.icon;
  return (
    <NavLink
      to={item.to}
      end={item.end}
      className={({ isActive }) =>
        cn(
          "group relative flex min-h-[var(--shell-pill-height)] items-center gap-3 rounded-2xl px-[var(--shell-nav-pad-x)] text-[length:var(--shell-nav-size)] font-semibold transition-all duration-200",
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
            <Icon className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" strokeWidth={isActive ? 2.15 : 1.8} />
          </span>
          <span className="min-w-0 flex-1 truncate">{item.label}</span>
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
  useEffect(() => { if (searchOpen) setTimeout(() => searchRef.current?.focus(), 50); }, [searchOpen]);

  return (
    <header className="sticky top-0 z-40 border-b border-hairline/70 bg-background/88 px-[var(--content-pad-inline)] py-4 backdrop-blur-2xl supports-[backdrop-filter]:bg-background/78 lg:py-5 dark:border-white/[0.05]">
      <div className="mx-auto flex w-full items-center gap-4" style={{ maxWidth: "min(var(--content-max-width), calc(100vw - (var(--content-outer-gap) * 2)))" }}>
        <NavLink to="/" aria-label="Deluno home" className="flex shrink-0 items-center no-underline lg:hidden">
          <AppMark />
        </NavLink>

        <div className="min-w-0 flex-1">
          <p className="hidden text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground min-[520px]:block">{subtitle}</p>
          <h1 className="mt-0.5 truncate font-display text-[length:var(--type-title-sm)] font-semibold tracking-tight text-foreground sm:mt-1 sm:text-[length:var(--type-title-md)]">
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
      <rect x="5.5" y="9" width="19" height="12" rx="2.5" stroke="white" strokeWidth="1.3" strokeOpacity="0.7" />
      <rect x="5.5" y="11.25" width="2.5" height="2.25" rx="0.5" fill="white" fillOpacity="0.6" />
      <rect x="5.5" y="16.5" width="2.5" height="2.25" rx="0.5" fill="white" fillOpacity="0.6" />
      <rect x="22" y="11.25" width="2.5" height="2.25" rx="0.5" fill="white" fillOpacity="0.6" />
      <rect x="22" y="16.5" width="2.5" height="2.25" rx="0.5" fill="white" fillOpacity="0.6" />
      <path d="M13.75 12.75L19 15L13.75 17.25V12.75Z" fill="white" fillOpacity="0.95" />
      <defs>
        <linearGradient id="mark-bg" x1="0" y1="0" x2="30" y2="30" gradientUnits="userSpaceOnUse">
          <stop style={{ stopColor: "hsl(var(--primary))" }} />
          <stop offset="1" style={{ stopColor: "hsl(var(--primary-2))" }} />
        </linearGradient>
      </defs>
    </svg>
  );
}

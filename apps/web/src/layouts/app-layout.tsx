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
import { useEffect, useMemo, useRef, useState, type CSSProperties, type FormEvent } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
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
import { getGlobalShortcuts } from "../lib/command-registry";
import { attentionTotal, type AttentionSnapshot, useAttention } from "../lib/use-attention";
import { useAuth, type UserProfile } from "../lib/use-auth";
import { DENSITY_LABELS, DensityProvider, useDensity, type Density } from "../lib/use-density";
import { SignalRProvider } from "../lib/use-signalr";
import { cn } from "../lib/utils";
import { commandPaletteShortcut } from "../lib/platform-shortcuts";
import { UnsavedChangesProvider } from "../components/shell/unsaved-changes-provider";
import { configurationNavAreas, maintenanceNavItems, settingsPageMeta } from "../components/app/settings-shell";
import { DelunoNavGlyph, type DelunoNavGlyphKind } from "../components/shell/deluno-nav-glyph";
import { TOOLBAR_ACCENT_COLOURS, type ToolbarAccent } from "../components/ui/page-toolbar";

/** The shape both sidebar area lists share. */
interface NavArea {
  match: (path: string) => boolean;
  label: string;
  icon: DelunoNavGlyphKind;
  to: string;
  accent: ToolbarAccent;
  tabsInToolbar: boolean;
  items: readonly { to: string; label: string; end: boolean }[];
}

function navAccentStyle(accent: ToolbarAccent) {
  const colour = TOOLBAR_ACCENT_COLOURS[accent];
  return {
    "--nav-accent": `hsl(${colour})`,
    "--nav-accent-soft": `hsl(${colour} / 0.14)`,
    "--nav-accent-border": `hsl(${colour} / 0.3)`
  } as CSSProperties;
}

function isEditableTarget(target: EventTarget | null) {
  if (!(target instanceof HTMLElement)) return false;
  const tag = target.tagName;
  if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return true;
  return target.isContentEditable;
}

const libraryNav = [
  { to: "/", label: "Dashboard", icon: "dashboard", end: true, attention: "none" as const, accent: "blue" as const },
  { to: "/movies", label: "Movies", icon: "movies", end: false, attention: "movies" as const, accent: "yellow" as const },
  { to: "/tv", label: "TV Shows", icon: "shows", end: false, attention: "tv" as const, accent: "yellow" as const },
  { to: "/calendar", label: "Schedule", icon: "schedule", end: false, attention: "none" as const, accent: "blue" as const }
] as const;

const operationsNav = [
  { to: "/queue", label: "Transfers", icon: "transfers", end: false, attention: "activity" as const, accent: "orange" as const },
  { to: "/activity", label: "Activity", icon: "activity", end: false, attention: "activity" as const, accent: "green" as const }
] as const;

const densityChoices: Density[] = ["compact", "comfortable", "spacious", "expanded"];

const routeMeta = [
  { match: (path: string) => path === "/", title: "Dashboard", subtitle: "Your movies and shows, in one place" },
  { match: (path: string) => path.startsWith("/movies"), title: "Movies", subtitle: "Manage and grow your movie library" },
  { match: (path: string) => path.startsWith("/tv"), title: "TV Shows", subtitle: "Manage your shows, episodes, and upgrades" },
  { match: (path: string) => path.startsWith("/calendar"), title: "Schedule", subtitle: "Upcoming releases and retry windows" },
  { match: (path: string) => path.startsWith("/queue"), title: "Transfers", subtitle: "Follow downloads through processing and safe import" },
  { match: (path: string) => path.startsWith("/indexers"), title: "Find & Download", subtitle: "Search sources and download clients Deluno uses" },
  { match: (path: string) => path.startsWith("/search-cycles") || path.startsWith("/settings/automation"), title: "Automation & Recovery", subtitle: "What Deluno searches for on a schedule, and how it recovers when a download fails" },
  { match: (path: string) => path.startsWith("/activity"), title: "Activity", subtitle: "The permanent record of what happened and why" },
  { match: (path: string) => path.startsWith("/settings/policy-sets") || path.startsWith("/settings/profiles") || path.startsWith("/settings/quality") || path.startsWith("/settings/custom-formats"), title: "Library Profiles", subtitle: "Reusable settings each library inherits for quality, searching, upgrades, and routing" },
  { match: (path: string) => path.startsWith("/settings/lists"), title: "Discover Media", subtitle: "Bring movies and shows in from watchlists and curated feeds" },
  { match: (path: string) => path.startsWith("/settings/general") || path.startsWith("/settings/notifications") || path.startsWith("/settings/ui") || path.startsWith("/settings/migration"), title: "Preferences", subtitle: "How you want Deluno to behave, look, and tell you things" },
  // Every /settings route is named by settingsPageMeta, which is the single
  // source of truth for settings page names. This used to be a handful of
  // coarse prefix matches ending in a "Media Management" catch-all, which is
  // why /settings itself was titled after a different page entirely.
  {
    match: (path: string) => settingsPageMeta.some((item) => item.match(path)),
    title: (path: string) => settingsPageMeta.find((item) => item.match(path))?.title ?? "Settings",
    subtitle: (path: string) => settingsPageMeta.find((item) => item.match(path))?.description ?? ""
  },
  // The guided setup lives in the System area but is not the System page: it
  // was titled "System — how this installation is doing", which named the
  // wrong thing on the first screen a new user studies (#254).
  { match: (path: string) => path.startsWith("/setup-guide"), title: "Guided setup", subtitle: "Get the essentials in place — folders, quality, and how releases are chosen" },
  { match: (path: string) => path.startsWith("/system"), title: "System", subtitle: "How this installation is doing — health, backups, updates, and audit" }
];

/**
 * `/movies/{id}` and `/tv/{id}` render their own `h1`: the title of the thing.
 * The named sub-routes under those prefixes are ordinary pages and must not be
 * mistaken for one, or the topbar yields its heading to a page without one.
 */
const MEDIA_SUB_ROUTES = new Set(["episodes", "wanted", "upgrades", "import", "library"]);
const TOOLBAR_ROUTE_PREFIXES = ["/activity", "/calendar", "/indexers", "/queue", "/search-cycles", "/settings", "/system"];

function isDetailRoute(pathname: string) {
  const match = /^\/(?:movies|tv)\/([^/]+)$/.exec(pathname);
  return match !== null && !MEDIA_SUB_ROUTES.has(match[1]);
}

function isToolbarPage(pathname: string) {
  return TOOLBAR_ROUTE_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`))
    || /^\/(?:movies|tv)\/[^/]+(?:\/|$)/.test(pathname);
}

export function AppLayout() {
  return <AppLayoutInner />;
}

function AppLayoutInner() {
  const { token } = useAuth();
  const [queryClient] = useState(() => new QueryClient({
    defaultOptions: {
      queries: { retry: 1 }
    }
  }));
  return (
    <QueryClientProvider client={queryClient}>
      <SignalRProvider accessToken={token}>
        <UnsavedChangesProvider>
          <AppLayoutContent />
        </UnsavedChangesProvider>
      </SignalRProvider>
    </QueryClientProvider>
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
  const globalShortcuts = getGlobalShortcuts();

  // Most entries name a fixed page. The settings entry resolves its name per
  // path from settingsPageMeta, so title and subtitle may be functions.
  const meta = useMemo(() => {
    const found = routeMeta.find((item) => item.match(location.pathname)) ?? routeMeta[0];
    const resolve = (value: string | ((path: string) => string)) =>
      typeof value === "function" ? value(location.pathname) : value;
    return { title: resolve(found.title), subtitle: resolve(found.subtitle) };
  }, [location.pathname]);

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

        <CommandPalette open={commandOpen} onOpenChange={setCommandOpen} theme={resolvedTheme} onToggleTheme={() => setTheme(resolvedTheme === "dark" ? "light" : "dark")} />
        <KeyboardHintOverlay open={helpOpen} onOpenChange={setHelpOpen} shortcuts={globalShortcuts.map((s) => ({ keys: s.keys, label: s.label, group: s.group }))} />

        <div className="min-h-dvh">
          <DesktopSidebar attention={attention} user={user} onLogout={logout} />

          <div className="min-w-0 pb-mobile-tabbar lg:ml-[var(--sidebar-width)] lg:pb-0">
            <ContentTopbar
              title={meta.title}
              subtitle={meta.subtitle}
              ownsHeading={!isDetailRoute(location.pathname)}
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
                // Toolbar pages use the same flush title → rail rhythm as the
                // settings reference page. Ordinary pages retain the normal
                // content breathing room.
                paddingTop: isToolbarPage(location.pathname) ? "0px" : "var(--content-pad-block)",
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
  const { pathname } = useLocation();
  const needsAttention = attentionTotal(attention);
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
    <aside className="fixed inset-y-0 left-0 z-50 hidden w-[var(--sidebar-width)] border-r border-sidebar-border bg-sidebar px-3 py-3 text-sidebar-foreground lg:flex lg:flex-col">
      <NavLink to="/" aria-label="Deluno home" className="flex min-h-[calc(var(--shell-pill-height)*1.45)] items-center gap-3 rounded-lg border border-sidebar-border bg-sidebar-accent/45 px-3 text-sidebar-foreground no-underline">
        <AppMark size={34} />
        <span className="min-w-0">
          <span className="block whitespace-nowrap font-display text-[length:var(--shell-brand-size)] font-bold tracking-[0.08em]">Deluno</span>
          <span className="block whitespace-nowrap text-[length:var(--shell-subtle-size)] font-medium text-muted-foreground">Media console</span>
        </span>
      </NavLink>

      <div className="mt-5 min-h-0 flex-1 overflow-x-hidden overflow-y-auto pr-1 [scrollbar-width:none] [&::-webkit-scrollbar]:hidden">
        <div className="mb-2 mt-5 border-t border-hairline/70 px-[var(--shell-nav-inset)] pt-4 text-[length:var(--shell-subtle-size)] font-semibold uppercase tracking-[0.13em] text-muted-foreground first:mt-0 first:border-t-0 first:pt-0">
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

        <div className="mb-2 mt-5 border-t border-hairline/70 px-[var(--shell-nav-inset)] pt-4 text-[length:var(--shell-subtle-size)] font-semibold uppercase tracking-[0.13em] text-muted-foreground first:mt-0 first:border-t-0 first:pt-0">
          Live operations
        </div>
        <nav aria-label="Live operational status" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
          {operationsNav.map((item) => (
            <SidebarItem
              key={item.to}
              item={item}
              count={attentionCount(attention, item.attention)}
            />
          ))}
        </nav>

        <div className="mb-2 mt-5 border-t border-hairline/70 px-[var(--shell-nav-inset)] pt-4 text-[length:var(--shell-subtle-size)] font-semibold uppercase tracking-[0.13em] text-muted-foreground first:mt-0 first:border-t-0 first:pt-0">
          Setup
        </div>
        <nav aria-label="Media Management" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
          <ConfigurationSidebarTree pathname={pathname} />
        </nav>

        <div className="mb-2 mt-5 border-t border-hairline/70 px-[var(--shell-nav-inset)] pt-4 text-[length:var(--shell-subtle-size)] font-semibold uppercase tracking-[0.13em] text-muted-foreground first:mt-0 first:border-t-0 first:pt-0">
          Deluno
        </div>
        <nav aria-label="System controls" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
          <MaintenanceSidebarTree pathname={pathname} />
        </nav>
      </div>

      {/*
        The headline used to be hardcoded to "All systems normal" and sat directly
        above a line reporting failed jobs — it claimed health while showing the
        opposite. It now follows the same signal the line below it reads, and
        that signal covers everything that needs a person (failed and
        dead-lettered jobs, unhealthy search sources), not failed jobs alone —
        it used to say "All good" beside a dead-lettered import (#250).
      */}
      <div className="mt-3 rounded-lg border border-sidebar-border bg-sidebar-accent/45 p-3">
        <div className="flex items-center gap-2">
          <span
            className={cn(
              "h-2 w-2 shrink-0 rounded-full",
              needsAttention > 0
                ? "bg-warning shadow-[0_0_12px_hsl(var(--warning)/0.8)]"
                : "bg-success shadow-[0_0_12px_hsl(var(--success)/0.8)]"
            )}
          />
          <span className="density-nowrap text-[length:var(--type-body-sm)] font-semibold text-foreground">
            {needsAttention > 0 ? "Needs a look" : "All good"}
          </span>
        </div>
        <p className="mt-2 text-[length:var(--shell-subtle-size)] text-muted-foreground">
          {needsAttention > 0 ? attentionSummary(attention) : "Nothing needs you"}
        </p>
      </div>

      <div className="group relative z-50 mt-3">
        <button
          type="button"
          className="flex min-h-[var(--shell-pill-height)] w-full items-center gap-3 rounded-lg border border-sidebar-border bg-sidebar-accent/45 px-3 text-left transition hover:border-primary/30 hover:bg-sidebar-accent"
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

/**
 * One row per area. An area whose pages all carry a `PageToolbar` shows no
 * children here — the toolbar tabs are the way between siblings, and listing
 * them twice made the sidebar and the page argue about the same job.
 */
function AreaRow({
  area,
  pathname,
  open,
  onToggle
}: {
  area: NavArea;
  pathname: string;
  open: boolean;
  onToggle: () => void;
}) {
  const showChildren = !area.tabsInToolbar && area.items.some((item) => item.to !== area.to);
  const areaIsActive = area.match(pathname);
  const accentStyle = navAccentStyle(area.accent);

  return (
    <div style={accentStyle}>
      <div className="flex min-h-[var(--shell-pill-height)] items-center gap-1">
        <NavLink
          to={area.to}
          className={({ isActive }) => cn(
            "group relative flex min-h-[var(--shell-pill-height)] min-w-0 flex-1 items-center gap-2.5 rounded-lg px-[var(--shell-nav-inset)] text-[length:var(--shell-nav-size)] font-semibold transition-colors duration-150",
            isActive || areaIsActive ? "text-[var(--nav-accent)]" : "text-muted-foreground hover:text-[var(--nav-accent)]"
          )}
        >
          {({ isActive }) => <>
            <span aria-hidden className={cn("absolute left-0 h-[calc(var(--shell-pill-height)*0.55)] w-[3px] rounded-r-full transition-colors", isActive || areaIsActive ? "bg-[var(--nav-accent)]" : "bg-transparent")} />
            <span
              className={cn(
                "flex h-[var(--shell-icon-col)] w-[var(--shell-icon-col)] shrink-0 items-center justify-center rounded-[8px] border transition-colors",
                isActive || areaIsActive
                  ? "border-[var(--nav-accent-border)] bg-transparent text-[var(--nav-accent)]"
                  : "border-hairline/70 bg-surface-2/70 text-[var(--nav-accent)] opacity-75 group-hover:border-[var(--nav-accent-border)] group-hover:opacity-100"
              )}
            >
              <DelunoNavGlyph kind={area.icon} className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" />
            </span>
            <span className="min-w-0 flex-1 whitespace-nowrap">{area.label}</span>
          </>}
        </NavLink>
        {showChildren ? (
          <button
            type="button"
            aria-label={`${open ? "Collapse" : "Expand"} ${area.label}`}
            aria-expanded={open}
            onClick={onToggle}
            className="flex h-[var(--shell-pill-height)] w-10 shrink-0 items-center justify-center rounded-lg text-muted-foreground transition hover:bg-muted/50 hover:text-foreground"
          >
            <ChevronRight className={cn("h-4 w-4 transition-transform duration-200", open && "rotate-90 text-[var(--nav-accent)]")} />
          </button>
        ) : null}
      </div>
      {showChildren && open ? (
        <div className="ml-[calc((var(--shell-nav-pad-x)*0.55)_+_1rem)] mt-0.5 space-y-0.5 pl-2">
          {area.items.filter((item) => item.to !== area.to).map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              title={item.label}
              className={({ isActive }) => cn(
                "flex min-h-7 min-w-0 items-center gap-2 rounded-lg px-2.5 py-1.5 text-[length:calc(var(--shell-nav-size)*0.9)] font-medium transition",
                isActive ? "text-[var(--nav-accent)]" : "text-muted-foreground hover:text-[var(--nav-accent)]"
              )}
            >
              {({ isActive }) => <>
                <span aria-hidden className={cn("h-1.5 w-1.5 shrink-0 rounded-full", isActive ? "bg-[var(--nav-accent)]" : "bg-muted-foreground/35")} />
                <span className="min-w-0 truncate">{item.label}</span>
              </>}
            </NavLink>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function useAreaTree(areas: readonly NavArea[], pathname: string) {
  const activeArea = areas.find((area) => area.match(pathname));
  const [openAreas, setOpenAreas] = useState<Set<string>>(() => new Set(activeArea ? [activeArea.label] : []));

  useEffect(() => {
    if (activeArea) setOpenAreas((current) => new Set([...current, activeArea.label]));
  }, [activeArea?.label]);

  return {
    openAreas,
    toggleArea: (label: string) =>
      setOpenAreas((current) => {
        const next = new Set(current);
        if (next.has(label)) next.delete(label);
        else next.add(label);
        return next;
      })
  };
}

function ConfigurationSidebarTree({ pathname }: { pathname: string }) {
  const { openAreas, toggleArea } = useAreaTree(configurationNavAreas, pathname);
  return (
    <div className="space-y-1">
      {configurationNavAreas.map((area) => (
        <AreaRow key={area.label} area={area} pathname={pathname} open={openAreas.has(area.label)} onToggle={() => toggleArea(area.label)} />
      ))}
    </div>
  );
}

function MaintenanceSidebarTree({ pathname }: { pathname: string }) {
  const { openAreas, toggleArea } = useAreaTree(maintenanceNavItems, pathname);
  return (
    <div className="space-y-1">
      {maintenanceNavItems.map((area) => (
        <AreaRow key={area.label} area={area} pathname={pathname} open={openAreas.has(area.label)} onToggle={() => toggleArea(area.label)} />
      ))}
    </div>
  );
}

function SidebarItem({
  item,
  count
}: {
  item: { to: string; label: string; icon: DelunoNavGlyphKind; end: boolean; accent: ToolbarAccent };
  count: number;
}) {
  const accentStyle = navAccentStyle(item.accent);

  return (
    <NavLink
      to={item.to}
      end={item.end}
      style={accentStyle}
      className={({ isActive }) =>
        cn(
          "group relative flex min-h-[var(--shell-pill-height)] items-center gap-2.5 rounded-lg px-[var(--shell-nav-inset)] text-[length:var(--shell-nav-size)] font-semibold transition-colors duration-150",
          isActive ? "text-[var(--nav-accent)]" : "text-muted-foreground hover:text-[var(--nav-accent)]"
        )
      }
    >
      {({ isActive }) => (
        <>
          <span aria-hidden className={cn("absolute left-0 h-[calc(var(--shell-pill-height)*0.55)] w-[3px] rounded-r-full transition-colors", isActive ? "bg-[var(--nav-accent)]" : "bg-transparent")} />
          <span
            className={cn(
                "flex h-[var(--shell-icon-col)] w-[var(--shell-icon-col)] shrink-0 items-center justify-center rounded-[8px] border transition-colors",
                isActive
                ? "border-[var(--nav-accent-border)] bg-transparent text-[var(--nav-accent)]"
                : "border-hairline/70 bg-surface-2/70 text-[var(--nav-accent)] opacity-75 group-hover:border-[var(--nav-accent-border)] group-hover:opacity-100"
            )}
          >
            <DelunoNavGlyph kind={item.icon} className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" />
          </span>
          <span className="min-w-0 flex-1 whitespace-nowrap">{item.label}</span>
          {count > 0 ? (
            <span className={cn("flex h-[calc(var(--shell-pill-height)*0.42)] min-w-[calc(var(--shell-pill-height)*0.42)] shrink-0 items-center justify-center rounded-full px-1.5 text-[length:var(--shell-nav-badge-size)] font-bold tabular-nums", isActive ? "bg-[var(--nav-accent)] text-background" : "bg-surface-2 text-muted-foreground")}>
              {count}
            </span>
          ) : null}
        </>
      )}
    </NavLink>
  );
}

function TopbarTitle({ as: Tag, children }: { as: "h1" | "p"; children: React.ReactNode }) {
  return (
    <Tag className="mt-0.5 font-display text-[length:var(--type-title-sm)] font-semibold leading-tight tracking-tight text-foreground sm:mt-1 sm:text-[length:var(--type-title-md)]">
      {children}
    </Tag>
  );
}

function ContentTopbar({
  title,
  subtitle,
  ownsHeading,
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
  ownsHeading: boolean;
}) {
  const searchRef = useRef<HTMLInputElement>(null);
  const commandShortcut = commandPaletteShortcut();
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
          {/* The topbar is the page heading everywhere except a detail route, where
              it names the section and the page's own title is the h1. Two h1s on one
              document is what this avoids. */}
          <TopbarTitle as={ownsHeading ? "h1" : "p"}>{title}</TopbarTitle>
        </div>

        <button
          type="button"
          onClick={onOpenCommand}
          aria-label={`Search & navigate (${commandShortcut.label})`}
          aria-keyshortcuts={commandShortcut.ariaKeyshortcuts}
          className="hidden min-h-[var(--shell-pill-height)] items-center gap-2 rounded-lg border border-hairline/70 bg-card/75 px-4 text-left text-[length:var(--shell-nav-size)] font-medium text-muted-foreground transition hover:border-primary/30 hover:bg-muted/40 hover:text-foreground md:flex"
        >
          <Search className="h-[var(--shell-icon-size-sm)] w-[var(--shell-icon-size-sm)]" />
          <span className="hidden xl:inline">Search &amp; navigate...</span>
          <kbd className="hidden rounded border border-hairline bg-background/70 px-1.5 py-0.5 font-mono text-[length:var(--shell-kbd-size)] text-muted-foreground/70 xl:inline">{commandShortcut.label}</kbd>
        </button>

        <Button type="button" variant="ghost" size="icon" onClick={() => setSearchOpen(true)} aria-label="Search & navigate" className="md:hidden">
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
            Open search &amp; navigate
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

/** What the pill says when something needs a person — the specific thing, not a total. */
function attentionSummary(attention: AttentionSnapshot): string {
  const parts: string[] = [];
  if (attention.failedJobs > 0) {
    const gaveUp = attention.deadLetteredJobs > 0 ? ` (${attention.deadLetteredJobs} gave up)` : "";
    parts.push(`${attention.failedJobs} failed job${attention.failedJobs === 1 ? "" : "s"}${gaveUp}`);
  }
  if (attention.indexerAlerts > 0) {
    parts.push(`${attention.indexerAlerts} search source${attention.indexerAlerts === 1 ? "" : "s"} unhealthy`);
  }
  return parts.join(" · ");
}

function attentionCount(attention: ReturnType<typeof useAttention>, key: "none" | "movies" | "tv" | "indexers" | "activity") {
  if (key === "movies") return attention.movieWanted;
  if (key === "tv") return attention.tvWanted;
  if (key === "indexers") return attention.indexerAlerts;
  if (key === "activity") return attention.failedJobs;
  return 0;
}

function AppMark({ size = 30 }: { size?: number }) {
  const markColor = "#FFD15A";
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 1024 1024"
      fill="none"
      aria-hidden
      style={{
        filter: "drop-shadow(0 2px 8px hsl(var(--primary)/0.28))",
        flexShrink: 0,
      }}
    >
      <rect width="1024" height="1024" rx="228" fill="url(#mark-bg)" />
      <rect x="76" y="76" width="872" height="872" rx="192" stroke={markColor} strokeOpacity="0.38" strokeWidth="20" />
      <path d="M256 650C197 520 255 363 397 294C563 213 754 289 823 461" stroke={markColor} strokeWidth="52" strokeLinecap="round" />
      <path d="M823 461L734 431" stroke={markColor} strokeWidth="52" strokeLinecap="round" />
      <path d="M823 461L787 375" stroke={markColor} strokeWidth="52" strokeLinecap="round" />
      <circle cx="256" cy="650" r="30" fill={markColor} />
      <text x="512" y="635" textAnchor="middle" fontFamily="Inter, Arial, sans-serif" fontSize="350" fontWeight="900" fill={markColor}>D</text>
      <defs>
        <radialGradient id="mark-bg" cx="0" cy="0" r="1" gradientUnits="userSpaceOnUse" gradientTransform="translate(705 184) rotate(124) scale(836)">
          <stop offset="0%" stopColor="#2A2618" />
          <stop offset="44%" stopColor="#111821" />
          <stop offset="100%" stopColor="#070A0F" />
        </radialGradient>
      </defs>
    </svg>
  );
}

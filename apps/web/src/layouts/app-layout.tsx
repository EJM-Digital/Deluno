import {
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
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type CSSProperties, type FormEvent } from "react";
import { QueryClientProvider } from "@tanstack/react-query";
import { useHotkeys } from "react-hotkeys-hook";
import { NavLink, useLocation, useNavigate, useRevalidator } from "react-router-dom";
import { CommandPalette } from "../components/shell/command-palette";
import { KeyboardHintOverlay } from "../components/shell/keyboard-hint-overlay";
import { MobileShellNav } from "../components/shell/mobile-shell-nav";
import { NavigationProgress } from "../components/shell/navigation-progress";
import { queryClient } from "../lib/route-cache";
import { prefetchHandlers, prefetchMainRoutesWhenIdle } from "../lib/route-prefetch";
import { PageTransition } from "../components/shell/motion";
import { Toaster, realtimeToast } from "../components/shell/toaster";
import { WsStatusBadge } from "../components/shell/ws-status-badge";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { getGlobalShortcuts } from "../lib/command-registry";
import { useAttention } from "../lib/use-attention";
import { useCutValueTitles } from "../lib/use-cut-value-titles";
import { authedFetch, useAuth, type UserProfile } from "../lib/use-auth";
import { hostTitle, useHostName } from "../lib/use-host-name";
import { useSidebarCollapsed } from "../lib/use-sidebar-collapsed";
import { RealtimeGroups, SignalRProvider, useSignalREvent, useSignalRResync } from "../lib/use-signalr";
import { cn } from "../lib/utils";
import { DisplayPreferencesProvider } from "../lib/display-preferences";
import { commandPaletteShortcut } from "../lib/platform-shortcuts";
import { UnsavedChangesProvider } from "../components/shell/unsaved-changes-provider";
import { configurationNavAreas, maintenanceNavItems, settingsPageMeta } from "../components/app/settings-shell";
import { DelunoNavGlyph, type DelunoNavGlyphKind } from "../components/shell/deluno-nav-glyph";
import { AppMark } from "../components/shell/app-mark";
import { NotificationsBell } from "../components/shell/notifications-bell";
import { OutageBanner } from "../components/app/outage-banner";
import { TOPBAR_CONTROL, TOPBAR_ICON, TOPBAR_ICON_BUTTON } from "./topbar-tokens";

/** The shape both sidebar area lists share. */
interface NavArea {
  match: (path: string) => boolean;
  label: string;
  icon: DelunoNavGlyphKind;
  to: string;
  tabsInToolbar: boolean;
  items: readonly { to: string; label: string; end: boolean }[];
}

/**
 * Navigation is monochrome and the accent marks only where you are (#290).
 *
 * Six per-area hues used to be lit at rest, and three of them were the exact
 * values of `--success`, `--warning` and `--info`. Colour was doing two
 * incompatible jobs: green meant *Healthy* on the dashboard and *Find &
 * Download* in the sidebar, permanently. Healthy is a colour you scan for the
 * absence of, so a permanently part-green sidebar made that scan meaningless.
 * Hue now belongs to state, and the one accent left is the brand blue that
 * buttons, links and focus rings already use.
 *
 * `--page-accent` keeps its name and its job from #256 — a toolbar's "New …"
 * and the empty-state button for the same action are still one colour — it is
 * simply the same colour everywhere now.
 */
const NAV_ACCENT_STYLE = {
  "--nav-accent": "hsl(var(--primary))",
  "--nav-accent-soft": "hsl(var(--primary) / 0.14)",
  "--nav-accent-border": "hsl(var(--primary) / 0.3)",
  "--page-accent": "hsl(var(--primary))",
  "--page-accent-image": "linear-gradient(to bottom, hsl(var(--primary)), hsl(var(--primary)))",
  "--page-accent-foreground": "hsl(var(--background))"
} as CSSProperties;

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
  { to: "/collections", label: "Collections", icon: "library", end: false, attention: "none" as const },
  { to: "/calendar", label: "Schedule", icon: "schedule", end: false, attention: "none" as const },
  { to: "/discover", label: "Discover", icon: "discover", end: false, attention: "none" as const },
  // #753: a free-text search across every enabled indexer, no title required.
  { to: "/search", label: "Search", icon: "search", end: false, attention: "none" as const }
] as const;

const operationsNav = [
  { to: "/queue", label: "Transfers", icon: "transfers", end: false, attention: "transfers" as const },
  { to: "/activity", label: "Activity", icon: "activity", end: false, attention: "activity" as const }
] as const;

const routeMeta = [
  { match: (path: string) => path === "/", title: "Dashboard", subtitle: "Your movies and shows, in one place" },
  { match: (path: string) => path.startsWith("/movies"), title: "Movies", subtitle: "Manage and grow your movie library" },
  { match: (path: string) => path.startsWith("/tv"), title: "TV Shows", subtitle: "Manage your shows, episodes, and upgrades" },
  { match: (path: string) => path.startsWith("/collections"), title: "Collections", subtitle: "Follow movie franchises and keep their libraries current" },
  { match: (path: string) => path.startsWith("/calendar"), title: "Schedule", subtitle: "Upcoming releases and retry windows" },
  { match: (path: string) => path.startsWith("/discover"), title: "Discover", subtitle: "Browse what TMDb and your lists recommend, and add what you want" },
  { match: (path: string) => path === "/search" || path.startsWith("/search/"), title: "Search", subtitle: "Search every enabled indexer at once, no title required" },
  { match: (path: string) => path.startsWith("/queue"), title: "Transfers", subtitle: "Follow downloads through processing and safe import" },
  { match: (path: string) => path.startsWith("/indexers"), title: "Find & Download", subtitle: "Search sources and download clients Deluno uses" },
  { match: (path: string) => path.startsWith("/search-cycles"), title: "Automation & Recovery", subtitle: "What Deluno searches for on a schedule, and how it recovers when a download fails" },
  { match: (path: string) => path.startsWith("/activity"), title: "Activity", subtitle: "The permanent record of what happened and why" },
  // The area used to be called "Library Profiles" in the topbar and "Quality
  // Profiles" in the sidebar, while owning tabs of both names — so no name
  // told you whether you were in the parent or one of its parts (#255). It is
  // named for what it governs, matching the sidebar's other "X & Y" areas.
  { match: (path: string) => path.startsWith("/settings/policy-sets") || path.startsWith("/settings/profiles") || path.startsWith("/settings/quality") || path.startsWith("/settings/custom-formats") || path.startsWith("/settings/release-rules"), title: "Quality & Release", subtitle: "How Deluno judges a release — and the Library Profiles that apply those choices to a library" },
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
const MEDIA_SUB_ROUTES = new Set(["season-pass", "wanted", "upgrades", "import", "library"]);
const TOOLBAR_ROUTE_PREFIXES = ["/activity", "/calendar", "/collections", "/discover", "/indexers", "/queue", "/search-cycles", "/settings", "/system"];

function isDetailRoute(pathname: string) {
  const match = /^\/(?:movies|tv)\/([^/]+)$/.exec(pathname);
  return match !== null && !MEDIA_SUB_ROUTES.has(match[1]);
}

function isToolbarPage(pathname: string) {
  return TOOLBAR_ROUTE_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`))
    || /^\/(?:movies|tv)\/[^/]+(?:\/|$)/.test(pathname);
}

export function AppLayout() {
  useCutValueTitles();
  return <AppLayoutInner />;
}

function AppLayoutInner() {
  const { token } = useAuth();
  // The one cache the route loaders and the screens share lives in
  // lib/route-cache.ts (#1081). What it holds belongs to whoever signed in, so
  // it goes when they sign out.
  useEffect(() => {
    if (!token) queryClient.clear();
  }, [token]);
  // Once the app is idle after sign-in, the main screens' code is fetched so
  // the first click on any of them finds it already there.
  useEffect(() => {
    if (token) prefetchMainRoutesWhenIdle();
  }, [token]);
  return (
    <QueryClientProvider client={queryClient}>
      <SignalRProvider accessToken={token}>
        <DisplayPreferencesProvider token={token}>
          <UnsavedChangesProvider>
            <AppLayoutContent />
          </UnsavedChangesProvider>
        </DisplayPreferencesProvider>
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
  const revalidator = useRevalidator();
  // The tab says which computer this Deluno is on (#1101); the sign-in page keeps plain "Deluno".
  const hostName = useHostName();
  const sidebar = useSidebarCollapsed();
  useEffect(() => {
    document.title = hostTitle(hostName);
    return () => {
      document.title = "Deluno";
    };
  }, [hostName]);

  /**
   * The topbar toggle and the command palette's toggle both changed the theme
   * without saving it, so the choice never survived a settings reload and
   * could disagree with whatever Settings -> Interface still had saved
   * (#736). Persisting from the one place both toggles call keeps the saved
   * theme and the visible one from drifting apart; a failed save still
   * leaves the theme applied for this session.
   */
  const setThemeAndSave = useCallback((theme: string) => {
    setTheme(theme);
    void authedFetch("/api/settings", {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ uiTheme: theme })
    }).catch(() => undefined);
  }, [setTheme]);

  /*
    A reconnect whose gap is too large to replay means events were lost, so
    every screen has to fetch current state again. One route revalidation,
    mounted here rather than per route, covers every loader-backed page,
    including one with no realtime handler of its own. Returning to a hidden
    tab is not such a case: realtime keeps delivering while hidden (#641).
  */
  useSignalRResync(() => {
    void queryClient.invalidateQueries();
    revalidator.revalidate();
  });

  /*
    An import finishing is the end of the workflow the user started, and until
    now nothing announced it: a download completed, an import succeeded or
    failed, and the only way to find out was to go and look at Transfers
    (#264). The server has always published this; it just had no listener.

    Announced app-wide rather than on Transfers, because the point is to reach
    the user wherever they are — and it refreshes the attention counts so the
    sidebar agrees with what the toast just said.
  */
  useSignalREvent("DispatchImportCompleted", RealtimeGroups.Queue, (event) => {
    const title = event.releaseName || "A download";
    if (event.succeeded) {
      realtimeToast.success(`${title} is in your library.`, {
        action: { label: "Open Transfers", onClick: () => navigate("/queue") }
      });
    } else {
      realtimeToast.error(`Import failed for ${title}.${event.failureReason ? ` ${event.failureReason}` : ""}`, {
        action: { label: "See why", onClick: () => navigate("/queue") }
      });
    }
    void attention.refresh();
  });
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
    const go: Record<string, string> = { o: "/", m: "/movies", t: "/tv", q: "/queue", i: "/indexers", x: "/search-cycles", a: "/activity", c: "/calendar", d: "/discover", s: "/settings", y: "/system" };
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
      <div className="relative min-h-dvh overflow-x-hidden bg-background text-foreground">
        <a
          href="#main-content"
          className={cn(
            "sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-3 focus:z-100",
            "focus:inline-flex focus:items-center focus:gap-2 focus:rounded-xl",
            "focus:border focus:border-primary/50 focus:bg-background/95 focus:px-3 focus:py-2",
            "focus:text-sm focus:font-semibold focus:text-foreground focus:shadow-lg",
            "focus:outline-hidden focus-visible:ring-2 focus-visible:ring-primary"
          )}
        >
          Skip to content
        </a>

        <CommandPalette open={commandOpen} onOpenChange={setCommandOpen} theme={resolvedTheme} onToggleTheme={() => setThemeAndSave(resolvedTheme === "dark" ? "light" : "dark")} />
        <KeyboardHintOverlay open={helpOpen} onOpenChange={setHelpOpen} shortcuts={globalShortcuts.map((s) => ({ keys: s.keys, label: s.label, group: s.group }))} />

        {/* App-wide, not page-scoped: an outage pauses cleanup and searching
            everywhere, so the one banner that says so shows above every
            screen rather than living on System (#901). */}
        <OutageBanner />
        <NavigationProgress />

        {/* The narrow rail is one variable: the sidebar, the content beside it and the page footer all read it. */}
        <div className="min-h-dvh" style={sidebar.collapsed ? ({ "--sidebar-width": SIDEBAR_RAIL_WIDTH } as CSSProperties) : undefined}>
          <DesktopSidebar attention={attention} user={user} onLogout={logout} collapsed={sidebar.collapsed} onToggleCollapsed={sidebar.toggle} />

          <div className="min-w-0 pb-mobile-tabbar lg:ml-(--sidebar-width) lg:pb-0">
            <ContentTopbar
              title={meta.title}
              subtitle={meta.subtitle}
              ownsHeading={!isDetailRoute(location.pathname)}
              attention={attention}
              resolvedTheme={resolvedTheme}
              setTheme={setThemeAndSave}
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
                ...NAV_ACCENT_STYLE,
                paddingInline: "var(--content-pad-inline)",
                // Toolbar pages use the same flush title → rail rhythm as the
                // settings reference page. Ordinary pages retain the normal
                // content breathing room.
                paddingTop: isToolbarPage(location.pathname) ? "0px" : "var(--content-pad-block)",
                paddingBottom: "var(--content-pad-block)"
              }}
            >
              {/* Never wider than 2400px, centred in the room beside the sidebar, as Weir's main column is. */}
              <div className="mx-auto w-full max-w-[2400px]">
                <PageTransition />
              </div>
            </main>
          </div>
        </div>

        <MobileShellNav attention={attention} user={user} onLogout={logout} />
        <Toaster />
      </div>
  );
}

/* Group labels take Weir's spacing exactly (#1107): 20px above (plus the 5.6px between groups), 8px below
   (plus 5.6px before the first link), and a 19px line, so Deluno's and Weir's sidebars line up. */
/* On the rail the words go and the line above the group stays, as in Weir. */
const SIDEBAR_GROUP_LABEL_RAIL = "mx-[6px] p-0 text-[length:0px] leading-0";
const SIDEBAR_GROUP_LABEL =
  "mb-[13.6px] mt-[25.6px] border-t border-hairline/70 px-(--shell-nav-inset) pt-4 text-(length:--shell-subtle-size) font-semibold uppercase leading-[19px] tracking-[0.13em] text-muted-foreground first:mt-0 first:border-t-0 first:pt-0";

/** Weir's collapsed sidebar: 84px, icons only. */
const SIDEBAR_RAIL_WIDTH = "84px";

/** Whether the sidebar is the icon rail, for the rows inside it. */
const SidebarRailContext = createContext(false);

export function DesktopSidebar({
  attention,
  user,
  onLogout,
  collapsed,
  onToggleCollapsed
}: {
  attention: ReturnType<typeof useAttention>;
  user: UserProfile | null;
  onLogout: () => void;
  collapsed: boolean;
  onToggleCollapsed: () => void;
}) {
  const { changePassword } = useAuth();
  const hostName = useHostName();
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
    <SidebarRailContext.Provider value={collapsed}>
    <aside className="fixed inset-y-0 left-0 z-50 hidden w-(--sidebar-width) border-r border-sidebar-border bg-sidebar px-3 py-3 text-sidebar-foreground lg:flex lg:flex-col" data-collapsed={collapsed}>
      {/* On the seam between the menu and the page, outside the scrolling list so it never adds a scrollbar. */}
      <button
        type="button"
        data-testid="sidebar-collapse"
        aria-label={collapsed ? "Expand navigation" : "Collapse navigation"}
        aria-expanded={!collapsed}
        onClick={onToggleCollapsed}
        className="absolute right-[-13.125px] top-[12.75px] z-3 flex h-[24.375px] w-[24.375px] items-center justify-center rounded-full border border-sidebar-border bg-background p-0 text-muted-foreground transition-colors duration-150 hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" aria-hidden="true">
          <path d={collapsed ? "m9.5 6 6 6-6 6" : "m14.5 6-6 6 6 6"} stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>
      <NavLink to="/" aria-label="Deluno home" className="flex min-h-[61px] items-center gap-3 rounded-lg border border-sidebar-border bg-sidebar-accent/45 px-3 text-sidebar-foreground no-underline">
        <AppMark size={34} />
        <span className={cn("min-w-0", collapsed && "hidden")}>
          <span className="block whitespace-nowrap font-display text-(length:--shell-brand-size) font-bold tracking-[0.08em]">Deluno</span>
          <span className="block truncate whitespace-nowrap text-(length:--shell-subtle-size) font-medium text-muted-foreground" title={hostName ?? undefined}>{hostName ?? "Media console"}</span>
        </span>
      </NavLink>

      <div className="mt-[17.6px] min-h-0 flex-1 overflow-x-hidden overflow-y-auto scrollbar-none [&::-webkit-scrollbar]:hidden">
        <div className={cn(SIDEBAR_GROUP_LABEL, collapsed && SIDEBAR_GROUP_LABEL_RAIL)}>
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

        <div className={cn(SIDEBAR_GROUP_LABEL, collapsed && SIDEBAR_GROUP_LABEL_RAIL)}>
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

        <div className={cn(SIDEBAR_GROUP_LABEL, collapsed && SIDEBAR_GROUP_LABEL_RAIL)}>
          Setup
        </div>
        <nav aria-label="Media Management" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
          <ConfigurationSidebarTree pathname={pathname} />
        </nav>

        <div className={cn(SIDEBAR_GROUP_LABEL, collapsed && SIDEBAR_GROUP_LABEL_RAIL)}>
          Deluno
        </div>
        <nav aria-label="System controls" className="space-y-[calc(var(--shell-nav-gap)*0.7)]">
          <MaintenanceSidebarTree pathname={pathname} />
        </nav>
      </div>

      <div className="group relative z-50 mt-3">
        <button
          type="button"
          className={cn("flex min-h-[42px] w-full items-center gap-3 rounded-lg border border-sidebar-border bg-sidebar-accent/45 py-[6px] text-left transition hover:border-primary/30 hover:bg-sidebar-accent", collapsed ? "justify-center px-0" : "px-3")}
          title={collapsed ? (user?.displayName ?? "User") : undefined}
          data-testid="user-menu"
        >
          <span className="flex h-(--shell-avatar-size) w-(--shell-avatar-size) shrink-0 items-center justify-center rounded-full bg-linear-to-br/srgb from-primary to-[hsl(var(--primary-2))] text-(length:--type-body-sm) font-bold text-primary-foreground">
            {user?.avatarInitials ?? "DU"}
          </span>
          {collapsed ? <span className="sr-only">{`${user?.displayName ?? "User"}, account menu`}</span> : (
            <span className="block min-w-0 flex-1">
              <span className="block truncate whitespace-nowrap text-[length:14px] font-semibold text-foreground">{user?.displayName ?? "User"}</span>
            </span>
          )}
          {collapsed ? null : <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" />}
        </button>
        {/* Invisible until the account button is hovered or focused, and it takes no clicks while invisible: it sat over
            the bottom-left of the page and swallowed clicks meant for what was under it. The strip across the 12px gap
            keeps the menu open while the pointer crosses from the button to it. */}
        <div className="pointer-events-none absolute bottom-0 left-[calc(100%+12px)] z-90 w-64 rounded-xl border border-hairline bg-popover opacity-0 shadow-lg transition before:absolute before:inset-y-0 before:-left-3 before:w-3 before:content-[''] group-focus-within:pointer-events-auto group-focus-within:opacity-100 group-hover:pointer-events-auto group-hover:opacity-100 dark:border-white/[0.07]">
          <div className="overflow-hidden rounded-xl">
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
      </div>
      {passwordOpen ? (
        <div className="fixed inset-0 z-100 flex items-center justify-center bg-background/65 p-4 backdrop-blur-xs">
          <form
            onSubmit={(event) => void handleChangePassword(event)}
            className="w-full max-w-md rounded-2xl border border-hairline bg-card p-5 shadow-lg dark:border-white/[0.07]"
          >
            <div className="flex items-start justify-between gap-(--grid-gap)">
              <div>
                <p className="text-(length:--section-eyebrow-size) font-bold uppercase tracking-[0.18em] text-muted-foreground">
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
    </SidebarRailContext.Provider>
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
  const rail = useContext(SidebarRailContext);
  const showChildren = !rail && !area.tabsInToolbar && area.items.some((item) => item.to !== area.to);
  const areaIsActive = area.match(pathname);

  return (
    <div style={NAV_ACCENT_STYLE}>
      <div className="flex min-h-(--shell-pill-height) items-center gap-1">
        <NavLink
          to={area.to}
          title={rail ? area.label : undefined}
          aria-label={rail ? area.label : undefined}
          {...prefetchHandlers(area.to)}
          className={({ isActive, isPending }) => cn(
            "group relative flex min-h-(--shell-pill-height) min-w-0 flex-1 items-center gap-2.5 rounded-lg text-(length:--shell-nav-size) font-semibold transition-colors duration-150",
            rail ? "justify-center px-0" : "px-(--shell-nav-inset)",
            isActive || isPending || areaIsActive ? "text-(--nav-accent)" : "text-muted-foreground hover:text-(--nav-accent)"
          )}
        >
          {({ isActive: active, isPending }) => { const isActive = active || isPending; return <>
            <span aria-hidden className={cn("absolute left-0 h-[calc(var(--shell-pill-height)*0.55)] w-[3px] rounded-r-full transition-colors", isActive || areaIsActive ? "bg-(--nav-accent)" : "bg-transparent")} />
            <span
              className={cn(
                "flex h-(--shell-icon-col) w-(--shell-icon-col) shrink-0 items-center justify-center rounded-[8px] border transition-colors",
                isActive || areaIsActive
                  ? "border-(--nav-accent-border) bg-transparent text-(--nav-accent)"
                  : "border-hairline/70 bg-surface-2/70 text-muted-foreground group-hover:border-(--nav-accent-border) group-hover:text-(--nav-accent)"
              )}
            >
              <DelunoNavGlyph kind={area.icon} className="h-(--shell-icon-size) w-(--shell-icon-size)" />
            </span>
            {rail ? null : <span className="min-w-0 flex-1 whitespace-nowrap">{area.label}</span>}
          </>; }}
        </NavLink>
        {showChildren ? (
          <button
            type="button"
            aria-label={`${open ? "Collapse" : "Expand"} ${area.label}`}
            aria-expanded={open}
            onClick={onToggle}
            className="flex h-(--shell-pill-height) w-10 shrink-0 items-center justify-center rounded-lg text-muted-foreground transition hover:bg-muted/50 hover:text-foreground"
          >
            <ChevronRight className={cn("h-4 w-4 transition-transform duration-200", open && "rotate-90 text-(--nav-accent)")} />
          </button>
        ) : null}
      </div>
      {showChildren && open ? (
        <div className="ml-[calc((var(--shell-nav-pad-x)*0.55)+1rem)] mt-0.5 space-y-0.5 pl-2">
          {area.items.filter((item) => item.to !== area.to).map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              title={item.label}
              {...prefetchHandlers(item.to)}
              className={({ isActive, isPending }) => cn(
                "flex min-h-7 min-w-0 items-center gap-2 rounded-lg px-2.5 py-1.5 text-[length:calc(var(--shell-nav-size)*0.9)] font-medium transition",
                isActive || isPending ? "text-(--nav-accent)" : "text-muted-foreground hover:text-(--nav-accent)"
              )}
            >
              {({ isActive: active, isPending }) => { const isActive = active || isPending; return <>
                <span aria-hidden className={cn("h-1.5 w-1.5 shrink-0 rounded-full", isActive ? "bg-(--nav-accent)" : "bg-muted-foreground/35")} />
                <span className="min-w-0 truncate">{item.label}</span>
              </>; }}
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
  item: { to: string; label: string; icon: DelunoNavGlyphKind; end: boolean };
  count: number;
}) {
  const rail = useContext(SidebarRailContext);
  return (
    <NavLink
      to={item.to}
      end={item.end}
      style={NAV_ACCENT_STYLE}
      title={rail ? item.label : undefined}
      aria-label={rail ? (count > 0 ? `${item.label}, ${count}` : item.label) : undefined}
      {...prefetchHandlers(item.to)}
      className={({ isActive, isPending }) =>
        cn(
          "group relative flex min-h-(--shell-pill-height) items-center gap-2.5 rounded-lg text-(length:--shell-nav-size) font-semibold transition-colors duration-150",
          rail ? "justify-center px-0" : "px-(--shell-nav-inset)",
          isActive || isPending ? "text-(--nav-accent)" : "text-muted-foreground hover:text-(--nav-accent)"
        )
      }
    >
      {({ isActive: active, isPending }) => {
        // A link answers the frame it is clicked: it takes the active look
        // while its screen is still opening.
        const isActive = active || isPending;
        return (
        <>
          <span aria-hidden className={cn("absolute left-0 h-[calc(var(--shell-pill-height)*0.55)] w-[3px] rounded-r-full transition-colors", isActive ? "bg-(--nav-accent)" : "bg-transparent")} />
          <span
            className={cn(
                "flex h-(--shell-icon-col) w-(--shell-icon-col) shrink-0 items-center justify-center rounded-[8px] border transition-colors",
                isActive
                ? "border-(--nav-accent-border) bg-transparent text-(--nav-accent)"
                : "border-hairline/70 bg-surface-2/70 text-muted-foreground group-hover:border-(--nav-accent-border) group-hover:text-(--nav-accent)"
            )}
          >
            <DelunoNavGlyph kind={item.icon} className="h-(--shell-icon-size) w-(--shell-icon-size)" />
          </span>
          {rail ? null : <span className="min-w-0 flex-1 whitespace-nowrap">{item.label}</span>}
          {count > 0 ? (
            <span aria-hidden={rail || undefined} className={cn("flex h-[calc(var(--shell-pill-height)*0.42)] min-w-[calc(var(--shell-pill-height)*0.42)] shrink-0 items-center justify-center rounded-full px-1.5 text-(length:--shell-nav-badge-size) font-bold tabular-nums", rail && "absolute right-[8px] top-[2px] h-[15px] min-w-[15px] px-1 text-[length:9px]", isActive ? "bg-(--nav-accent) text-background" : "bg-surface-2 text-muted-foreground")}>
              {count}
            </span>
          ) : null}
        </>
        );
      }}
    </NavLink>
  );
}

function TopbarTitle({ as: Tag, children }: { as: "h1" | "p"; children: React.ReactNode }) {
  return (
    <Tag className="mt-0.5 font-display text-(length:--type-title-sm) font-semibold leading-[1.15] tracking-tight text-foreground sm:mt-1 sm:text-(length:--type-title-md)">
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
  useEffect(() => { if (searchOpen) setTimeout(() => searchRef.current?.focus(), 50); }, [searchOpen]);

  return (
    <header className="sticky top-0 z-40 border-b border-hairline/70 px-(--content-pad-inline) py-3 backdrop-blur-2xl lg:py-3.5 dark:border-white/5">
      <div id="topbar-row" className="flex w-full min-w-0 items-center gap-(--grid-gap)">
        <NavLink to="/" aria-label="Deluno home" className="flex shrink-0 items-center no-underline lg:hidden">
          <AppMark />
        </NavLink>

        <div id="topbar-main" className="flex min-w-0 flex-1 items-center gap-[calc(var(--grid-gap)*2)]">
        <div className="min-w-0 flex-1">
          {/* One line, always: with the page's buttons beside it a long one wrapped to three and the header grew. */}
          <p title={typeof subtitle === "string" ? subtitle : undefined} className="truncate max-md:whitespace-normal max-md:break-words text-(length:--section-eyebrow-size) font-bold uppercase leading-[1.35] tracking-[0.14em] text-muted-foreground">{subtitle}</p>
          {/* The topbar is the page heading everywhere except a detail route, where
              it names the section and the page's own title is the h1. Two h1s on one
              document is what this avoids. A page that has tabs (the Dashboard) puts them on the title's own line,
              right after it, so the header never grows (James, 2 Oct 2026). */}
          <div className="flex min-w-0 items-baseline gap-[22px]">
            <div id="topbar-title" className="shrink-0"><TopbarTitle as={ownsHeading ? "h1" : "p"}>{title}</TopbarTitle></div>
            <div id="topbar-page-tabs" className="hidden min-w-0 flex-1 empty:hidden lg:block" />
          </div>
        </div>
        {/* The page's own buttons ("New indexer", "Refresh", "Manual import") sit here from `lg` up, right of the tabs
            and left of search, so no page spends a row on them (James, 2 Oct 2026). One height for all of them, as on
            the library shelf's row. */}
        <div id="topbar-page-actions" className="hidden shrink-0 items-center gap-2 empty:hidden lg:flex [&_:is(button,a)]:h-(--control-height)" />
        </div>

        {/*
          One row, one height, one icon size.

          These were three different tokens sitting side by side: the search
          pill on `--shell-pill-height` (42px), the icon buttons on
          `--control-height-icon` (38px), and the mobile search icon drawn a
          pixel larger than the bell beside it. Four pixels is invisible in a
          diff and obvious on screen. The two constants below are the row's
          only sizing; nothing here sets its own.

          The search box is also wider and shows its label from `lg` rather than
          `xl`. It is the only control here anybody uses on purpose, and it was
          the first one to collapse to a bare icon.
        */}
        <button
          type="button"
          onClick={onOpenCommand}
          aria-label={`Search & navigate (${commandShortcut.label})`}
          aria-keyshortcuts={commandShortcut.ariaKeyshortcuts}
          className={cn(
            TOPBAR_CONTROL,
            "hidden items-center gap-2.5 rounded-xl border border-hairline/70 bg-card/75 px-3.5 text-left text-(length:--shell-nav-size) font-medium text-muted-foreground transition md:flex",
            "lg:w-[16rem] xl:w-[20rem]",
            "hover:border-primary/30 hover:bg-muted/40 hover:text-foreground"
          )}
        >
          <Search className={cn(TOPBAR_ICON, "shrink-0")} />
          <span className="hidden lg:inline">Search &amp; navigate...</span>
          <kbd className="ml-auto hidden rounded-sm border border-hairline bg-background/70 px-1.5 py-0.5 font-mono text-(length:--shell-kbd-size) text-muted-foreground lg:inline">{commandShortcut.label}</kbd>
        </button>

        <button type="button" onClick={() => setSearchOpen(true)} aria-label="Search & navigate" className={cn(TOPBAR_ICON_BUTTON, "md:hidden")}>
          <Search className={TOPBAR_ICON} />
        </button>

        {/*
          The dot only appears when something has failed, so this button asks
          for attention. It used to jump straight to Activity and swallow
          whatever was asked of it (the panel below answers that); Show all
          activity is still the escape hatch to the full screen (#907).
        */}
        <NotificationsBell attention={attention} />

        <button type="button" className={cn(TOPBAR_ICON_BUTTON, "hidden md:inline-flex")} onClick={onOpenHelp} aria-label="Keyboard shortcuts">
          <HelpCircle className={TOPBAR_ICON} strokeWidth={1.75} />
        </button>

        <button
          type="button"
          onClick={() => setTheme(resolvedTheme === "dark" ? "light" : "dark")}
          className={cn(TOPBAR_ICON_BUTTON, "relative")}
          aria-label={resolvedTheme === "dark" ? "Switch to light mode" : "Switch to dark mode"}
        >
          <SunMedium className={cn("absolute transition duration-300", TOPBAR_ICON, resolvedTheme === "light" ? "scale-100 opacity-100" : "scale-75 opacity-0 -rotate-90")} strokeWidth={1.75} />
          <Moon className={cn("absolute transition duration-300", TOPBAR_ICON, resolvedTheme === "dark" ? "scale-100 opacity-100" : "scale-75 opacity-0 rotate-90")} strokeWidth={1.75} />
        </button>

        <WsStatusBadge className="hidden sm:inline-flex" />
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

function attentionCount(attention: ReturnType<typeof useAttention>, key: "none" | "movies" | "tv" | "indexers" | "activity" | "transfers") {
  if (key === "movies") return attention.movieWanted;
  // What Transfers lists under Needs attention (#556).
  if (key === "transfers") return attention.downloadsNeedingAction;
  if (key === "tv") return attention.tvWanted;
  if (key === "indexers") return attention.indexerAlerts;
  // Activity's badge counts what Activity can clear: failed jobs about a title. A failed background job is cleared on System (#1114).
  if (key === "activity") return attention.failedTitleJobs ?? attention.failedJobs;
  return 0;
}


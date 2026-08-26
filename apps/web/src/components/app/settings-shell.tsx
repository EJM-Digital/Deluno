import { createContext, useContext, useState, type ReactNode } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { FileInput, FolderTree, HelpCircle, Image, MapPinned, SlidersHorizontal, Tags, Workflow } from "lucide-react";
import { cn } from "../../lib/utils";
import { GlossaryModal } from "../ui/glossary-modal";
import { PageToolbar } from "../ui/page-toolbar";

export const librarySetupNavItems = [
  { to: "/settings/libraries", label: "Library & Storage", end: false, icon: <FolderTree aria-hidden="true" /> },
  { to: "/settings/media-management", label: "Media Naming", end: false, icon: <SlidersHorizontal aria-hidden="true" /> },
  { to: "/settings/import-policy", label: "Import Policy", end: false, icon: <FileInput aria-hidden="true" /> },
  { to: "/settings/processing", label: "Processing Workflow", end: false, icon: <Workflow aria-hidden="true" /> },
  { to: "/settings/destination-rules", label: "Final Destinations", end: false, icon: <MapPinned aria-hidden="true" /> },
  { to: "/settings/metadata", label: "Metadata & Files", end: false, icon: <Image aria-hidden="true" /> },
  { to: "/settings/tags", label: "Tags", end: false, icon: <Tags aria-hidden="true" /> }
] as const;

export const automationNavItems = [
  { to: "/search-cycles", label: "Automation", end: true },
  { to: "/search-cycles/missing", label: "Missing Searches", end: false },
  { to: "/search-cycles/upgrades", label: "Upgrades", end: false },
  { to: "/search-cycles/failed-downloads", label: "Failed Downloads", end: false }
] as const;

/**
 * Sidebar areas.
 *
 * `tabsInToolbar` is the rule that stops the sidebar and the page toolbar doing
 * the same job twice: when every page in an area carries a `PageToolbar` with
 * these items as its tabs, the sidebar shows the area as a single row and the
 * toolbar is the only way between siblings. Areas still holding an unconverted
 * page keep their children, because collapsing them would leave no way in.
 */
export const configurationNavAreas = [
  {
    match: (path: string) => path === "/settings" || librarySetupNavItems.some((item) => path.startsWith(item.to)),
    label: "Media Management",
    icon: "library",
    to: "/settings/libraries",
    tabsInToolbar: true,
    items: librarySetupNavItems
  },
  {
    match: (path: string) => path.startsWith("/settings/policy-sets") || path.startsWith("/settings/profiles") || path.startsWith("/settings/quality") || path.startsWith("/settings/custom-formats"),
    label: "Quality & Release",
    icon: "plans",
    to: "/settings/profiles",
    tabsInToolbar: true,
    items: [
      { to: "/settings/profiles", label: "Quality Profiles", end: false },
      { to: "/settings/quality", label: "Size Rules", end: false },
      { to: "/settings/custom-formats", label: "Release Preferences", end: false },
      { to: "/settings/policy-sets", label: "Library Profiles", end: false }
    ]
  },
  {
    match: (path: string) => path.startsWith("/indexers"),
    label: "Find & Download",
    icon: "connections",
    to: "/indexers/indexers",
    tabsInToolbar: true,
    items: [
      { to: "/indexers/indexers", label: "Indexers", end: false },
      { to: "/indexers/download-clients", label: "Download Clients", end: false },
      { to: "/indexers/library-routing", label: "Library Routing", end: false }
    ]
  },
  {
    match: (path: string) => path.startsWith("/search-cycles") || path.startsWith("/settings/automation"),
    label: "Automation & Recovery",
    icon: "automation",
    to: "/search-cycles",
    tabsInToolbar: true,
    items: automationNavItems
  },
  {
    match: (path: string) => path.startsWith("/settings/lists"),
    label: "Discover Media",
    icon: "discover",
    to: "/settings/lists",
    tabsInToolbar: true,
    items: [{ to: "/settings/lists", label: "Import Lists", end: false }]
  }
] as const;

/** Installation preferences, as toolbar tabs. Four is a bar; eleven was a scroller. */
export const systemSettingsNavItems = [
  { to: "/settings/general", label: "General", end: false },
  { to: "/settings/ui", label: "Interface", end: false },
  { to: "/settings/notifications", label: "Notifications", end: false },
  { to: "/settings/migration", label: "Migration", end: false }
] as const;

/** The running installation itself, as toolbar tabs. */
export const systemHealthNavItems = [
  { to: "/system", label: "Health", end: true },
  { to: "/system/audit", label: "Audit", end: false },
  { to: "/system/backups", label: "Backups", end: false },
  { to: "/system/updates", label: "Updates", end: false },
  { to: "/system/api", label: "API Access", end: false },
  { to: "/system/docs", label: "Help & Guides", end: false }
] as const;

/**
 * Installation-wide controls, never under library setup. Split in two: eleven
 * destinations under one heading is a scrolling tab bar, and the two halves
 * answer different questions — "how do I want Deluno to behave" versus "how is
 * this installation doing".
 */
export const maintenanceNavItems = [
  {
    match: (path: string) => systemSettingsNavItems.some((item) => path.startsWith(item.to)),
    label: "Preferences",
    icon: "setup",
    to: "/settings/general",
    tabsInToolbar: true,
    items: systemSettingsNavItems
  },
  {
    match: (path: string) => path.startsWith("/system") || path.startsWith("/setup-guide"),
    label: "System",
    icon: "system",
    to: "/system",
    tabsInToolbar: true,
    items: systemHealthNavItems
  }
] as const;

const SettingsWorkspaceContext = createContext(false);
const SystemWorkspaceContext = createContext(false);

const systemNavItems = [
  { to: "/system", label: "Health", end: true, tip: "Provider health, background jobs, and system status" },
  { to: "/system/audit", label: "Audit", end: false, tip: "Searchable event timeline and live activity stream" },
  { to: "/system/api", label: "API", end: false, tip: "Generate and revoke API keys for integrations and automation" },
  { to: "/system/docs", label: "Guide", end: false, tip: "Plain-English workflow guide for setup, routing, scoring, imports, and integrations" },
  { to: "/system/backups", label: "Backups", end: false, tip: "Manual backups, automatic schedule, restore preview, and downloads" },
  { to: "/system/updates", label: "Updates", end: false, tip: "Version status, update mode, download progress, and restart flow" }
] as const;

/**
 * The name of every settings page, and the one place it is defined.
 *
 * The topbar renders this as the page's h1 (see routeMeta in app-layout). The
 * page body deliberately does not repeat it — a page is named once. Before this
 * was consolidated there were two independent maps, which drifted: "/settings"
 * was "Media Management" in the topbar and "Setup overview" in the body.
 */
export const settingsPageMeta = [
  // Every page in this workspace keeps the same area identity in the topbar.
  // Page-specific guidance belongs in the page header or card below it; the
  // shell should not appear to change context when the user changes tabs.
  {
    match: (path: string) => path.startsWith("/settings/processing"),
    title: "Media Management",
    description: "Manage where your media lives, how it is named, and how it moves through Deluno.",
    chrome: "none"
  },
  {
    match: (path: string) => path === "/settings",
    title: "Setup Overview",
    description:
      "Guided configuration for your media library, quality policy, automation, and runtime behaviour."
  },
  {
    match: (path: string) => path.startsWith("/settings/import-policy"),
    title: "Media Management",
    description: "Manage where your media lives, how it is named, and how it moves through Deluno.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/media-management"),
    title: "Media Management",
    description: "Manage where your media lives, how it is named, and how it moves through Deluno.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/libraries"),
    title: "Media Management",
    description: "Manage where your media lives, how it is named, and how it moves through Deluno.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/destination-rules"),
    title: "Media Management",
    description: "Manage where your media lives, how it is named, and how it moves through Deluno.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/policy-sets"),
    title: "Library Profiles",
    description: "Configure the reusable settings each library inherits for quality, searching, upgrades, and routing.",
    // List → drawer pages carry their own toolbar; the topbar already names the page.
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/profiles"),
    title: "Quality Profiles",
    description: "The complete quality standard used by Library Profiles: tiers, sizes, release preferences, exclusions, and upgrades.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/quality"),
    title: "Size Rules",
    description: "File-size boundaries Library Profiles use to reject releases that are too small or too large.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/custom-formats"),
    title: "Release Preferences",
    description: "Preference rules for source, codec, HDR, language, group, and custom-format scoring used by Library Profiles.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/lists"),
    title: "Discover Media",
    description: "Watchlists and curated lists that can add the movies or shows you want Deluno to manage.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/automation"),
    title: "Automation & Recovery",
    description: "Control scheduled searches, retries, upgrades, and what happens after a failed download.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/migration"),
    title: "Migration Assistant",
    description: "Preview and import external media automation configuration without overwriting existing Deluno setup.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/metadata"),
    title: "Media Management",
    description: "Manage where your media lives, how it is named, and how it moves through Deluno.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/tags"),
    title: "Media Management",
    description: "Manage where your media lives, how it is named, and how it moves through Deluno.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/general"),
    title: "General",
    description: "Instance name, network address, port, and reverse-proxy routing.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/notifications"),
    title: "Notifications",
    description: "Outbound webhook events for grabs, imports, upgrades, and health alerts.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/ui"),
    title: "Interface",
    description: "Theme, density, default views, and how Deluno should feel on your display.",
    chrome: "none"
  }
] as const;

const systemPageMeta = [
  {
    match: (path: string) => path === "/system",
    title: "System Health",
    description: "Provider health, background jobs, and current system state."
  },
  {
    match: (path: string) => path.startsWith("/system/audit"),
    title: "Audit Timeline",
    description: "Searchable activity, live events, errors, imports, searches, and notifications."
  },
  {
    match: (path: string) => path.startsWith("/system/api"),
    title: "API Access",
    description: "Generate keys for trusted integrations, local scripts, dashboards, and external control-plane access."
  },
  {
    match: (path: string) => path.startsWith("/system/docs"),
    title: "Workflow Guide",
    description: "How Deluno should be configured, routed, scored, integrated, and recovered in plain English."
  },
  {
    match: (path: string) => path.startsWith("/system/backups"),
    title: "Backups",
    description: "Manual backups, automatic schedules, restore previews, and backup downloads."
  },
  {
    match: (path: string) => path.startsWith("/system/updates"),
    title: "Updates",
    description: "Version status, update channel, behaviour mode, download progress, and restart guidance."
  }
] as const;

export function SettingsWorkspaceLayout() {
  const location = useLocation();
  const meta = settingsPageMeta.find((item) => item.match(location.pathname)) ?? settingsPageMeta[0];
  const bare = "chrome" in meta && meta.chrome === "none";

  if (bare) {
    return (
      <SettingsWorkspaceContext.Provider value>
        <Outlet />
      </SettingsWorkspaceContext.Provider>
    );
  }

  return (
    <SettingsShell description={meta.description}>
      <SettingsWorkspaceContext.Provider value>
        <Outlet />
      </SettingsWorkspaceContext.Provider>
    </SettingsShell>
  );
}

/**
 * Every /system route hangs off this, so the toolbar lives here rather than
 * being repeated on six pages. The topbar already names the area, so there is
 * no page heading — same rule as every other converted page.
 */
export function SystemWorkspaceLayout() {
  return (
    <div className="flex flex-col gap-[var(--page-gap)]">
      <PageToolbar tabs={systemHealthNavItems} />
      <SystemWorkspaceContext.Provider value>
        <Outlet />
      </SystemWorkspaceContext.Provider>
    </div>
  );
}

export function SettingsShell({
  description,
  children
}: {
  description: string;
  children: ReactNode;
}) {
  const [glossaryOpen, setGlossaryOpen] = useState(false);

  if (useContext(SettingsWorkspaceContext)) {
    return <>{children}</>;
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      <GlossaryModal open={glossaryOpen} onOpenChange={setGlossaryOpen} />
      <div className="flex max-w-4xl items-start justify-between gap-[var(--grid-gap)]">
        <p className="max-w-3xl text-[length:var(--section-subtitle-size)] leading-relaxed text-muted-foreground">
          {description}
        </p>
        <button
          type="button"
          onClick={() => setGlossaryOpen(true)}
          className="shrink-0 rounded-lg p-1.5 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
          title="Open glossary"
          aria-label="Open glossary"
        >
          <HelpCircle className="h-5 w-5" />
        </button>
      </div>

      <div className="min-w-0 space-y-[var(--page-gap)]">
        {children}
      </div>
    </div>
  );
}

export function ConfigurationWorkspaceLayout() {
  return <Outlet />;
}

function SettingsNavLink({
  item,
  compact = false
}: {
  item: (typeof systemNavItems)[number];
  compact?: boolean;
}) {
  return (
    <NavLink
      to={item.to}
      end={item.end}
      title={item.tip}
      className={({ isActive }) =>
        cn(
          "group relative flex items-center rounded-xl border border-transparent font-semibold transition-all duration-200",
          compact ? "min-h-[calc(var(--shell-pill-height)*0.78)]" : "min-h-[var(--shell-pill-height)]",
          "text-muted-foreground hover:bg-surface-1 hover:text-foreground",
          isActive && "border-primary/30 bg-primary/12 text-foreground shadow-[inset_0_0_0_1px_hsl(var(--primary)/0.08)]"
        )
      }
      style={{
        fontSize: "var(--settings-nav-size)",
        paddingInline: compact ? "calc(var(--settings-nav-pad-x) * 1.08)" : "var(--settings-nav-pad-x)",
        paddingBlock: compact ? "calc(var(--settings-nav-pad-y) * 1.08)" : "var(--settings-nav-pad-y)"
      }}
    >
      {({ isActive }) => (
        <>
          {!compact ? (
            <span
              aria-hidden
              className={cn("absolute left-0 h-6 w-[3px] rounded-full", isActive ? "bg-primary" : "bg-transparent")}
            />
          ) : null}
          <span className="whitespace-nowrap">{item.label}</span>
        </>
      )}
    </NavLink>
  );
}

function SectionSubnav({
  groups
}: {
  groups: readonly { label: string; items: readonly (typeof systemNavItems)[number][] }[];
}) {
  return (
    <div className="no-scrollbar overflow-x-auto">
      <nav className="flex min-w-max items-center gap-1 rounded-2xl border border-hairline/80 bg-card/85 p-2 shadow-card dark:border-white/[0.07] dark:bg-white/[0.035]">
        {groups.map((group, groupIndex) => (
          <div key={group.label} className="flex items-center gap-1">
            {groupIndex > 0 ? <div className="mx-1.5 h-6 w-px bg-hairline/80" aria-hidden /> : null}
            {group.items.map((item) => (
              <SettingsNavLink key={item.to} item={item} compact />
            ))}
          </div>
        ))}
      </nav>
    </div>
  );
}

/**
 * Kept as a passthrough so the /system pages need no edit at their call site.
 * SystemWorkspaceLayout owns the toolbar; the page supplies only its content.
 */
export function SystemShell({ children }: { title?: string; description?: string; children: ReactNode }) {
  return <div className="flex flex-col gap-[var(--page-gap)]">{children}</div>;
}

import { createContext, useContext, useState, type ReactNode } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { HelpCircle } from "lucide-react";
import { cn } from "../../lib/utils";
import { GlossaryModal } from "../ui/glossary-modal";
import { PageToolbar } from "../ui/page-toolbar";

export const librarySetupNavItems = [
  { to: "/settings/libraries", label: "Library folders", end: false },
  { to: "/settings/media-management", label: "File handling & naming", end: false },
  { to: "/settings/processing", label: "Processing workflow", end: false },
  { to: "/settings/destination-rules", label: "Final destinations", end: false },
  { to: "/settings/metadata", label: "Metadata & sidecars", end: false },
  { to: "/settings/tags", label: "Tags", end: false }
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
    label: "Library setup",
    icon: "library",
    to: "/settings/libraries",
    tabsInToolbar: true,
    items: librarySetupNavItems
  },
  {
    match: (path: string) => path.startsWith("/indexers"),
    label: "Connections",
    icon: "connections",
    to: "/indexers",
    tabsInToolbar: true,
    items: [
      { to: "/indexers/indexers", label: "Indexers", end: false },
      { to: "/indexers/download-clients", label: "Download clients", end: false },
      { to: "/indexers/library-routing", label: "Library routing", end: false }
    ]
  },
  {
    match: (path: string) => path.startsWith("/settings/policy-sets") || path.startsWith("/settings/profiles") || path.startsWith("/settings/quality") || path.startsWith("/settings/custom-formats"),
    label: "Media Plans",
    icon: "plans",
    to: "/settings/policy-sets",
    tabsInToolbar: true,
    items: [
      { to: "/settings/policy-sets", label: "Plans", end: false },
      { to: "/settings/profiles", label: "Quality profiles", end: false },
      { to: "/settings/quality", label: "Size rules", end: false },
      { to: "/settings/custom-formats", label: "Release preferences", end: false }
    ]
  },
  {
    match: (path: string) => path.startsWith("/settings/lists"),
    label: "Discover media",
    icon: "discover",
    to: "/settings/lists",
    tabsInToolbar: true,
    items: [{ to: "/settings/lists", label: "Import lists", end: false }]
  },
  {
    match: (path: string) => path.startsWith("/settings/automation"),
    label: "Automation & recovery",
    icon: "recovery",
    to: "/settings/automation",
    tabsInToolbar: true,
    items: [{ to: "/settings/automation", label: "Search, retries & failed downloads", end: false }]
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
  { to: "/system/audit", label: "Activity", end: false },
  { to: "/system/backups", label: "Backups", end: false },
  { to: "/system/updates", label: "Updates", end: false },
  { to: "/system/api", label: "API access", end: false },
  { to: "/system/docs", label: "Help & guides", end: false }
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

const settingsPageMeta = [
  {
    match: (path: string) => path.startsWith("/settings/processing"),
    title: "Processing workflow",
    description: "Optional: let an external processor finish a file before Deluno imports and renames it.",
    chrome: "none"
  },
  {
    match: (path: string) => path === "/settings",
    title: "Setup overview",
    description:
      "Guided configuration for your media library, quality policy, automation, and runtime behaviour."
  },
  {
    match: (path: string) => path.startsWith("/settings/media-management"),
    title: "File handling & naming",
    description: "Set how completed files are named, linked, cleaned up, and imported into your library.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/libraries"),
    title: "Library folders",
    description: "Create the movie and TV libraries Deluno manages, and choose where each one lives.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/destination-rules"),
    title: "Final destinations",
    description: "Choose where completed movies and TV shows finally live after Deluno imports and names them.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/policy-sets"),
    title: "Media plans",
    description: "Configure the quality, size, release, language, and upgrade rules Deluno follows.",
    // List → drawer pages carry their own toolbar; the topbar already names the page.
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/profiles"),
    title: "Quality profiles",
    description: "Quality ladders and cutoff targets used by Media Plans.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/quality"),
    title: "Size rules",
    description: "File-size boundaries Media Plans use to reject releases that are too small or too large.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/custom-formats"),
    title: "Release preferences",
    description: "Preference rules for source, codec, HDR, language, group, and custom-format scoring used by Media Plans.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/lists"),
    title: "Import lists",
    description: "Watchlists and curated lists that can add the movies or shows you want Deluno to manage.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/automation"),
    title: "Automation & recovery",
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
    title: "Metadata & sidecars",
    description: "Language, ratings region, artwork, and optional files saved beside your media.",
    chrome: "none"
  },
  {
    match: (path: string) => path.startsWith("/settings/tags"),
    title: "Tags",
    description: "Labels used for filtering, routing, policies, and user organisation.",
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
    description: "Version status, update channel, behavior mode, download progress, and restart guidance."
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
    <SettingsShell title={meta.title} description={meta.description}>
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
  eyebrow = "Library setup",
  title,
  description,
  children
}: {
  eyebrow?: string;
  title: string;
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
      <div className="max-w-4xl">
        <div className="min-w-0">
          <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground">
            {eyebrow}
          </p>
          <div className="mt-2 flex items-center gap-3">
            <h1 className="font-display text-[length:var(--type-title-lg)] font-semibold tracking-tight text-foreground">
              {title}
            </h1>
            <button
              onClick={() => setGlossaryOpen(true)}
              className="rounded-lg p-1.5 text-muted-foreground hover:bg-secondary hover:text-foreground transition-colors"
              title="Open glossary"
            >
              <HelpCircle className="h-5 w-5" />
            </button>
          </div>
        </div>
        <p className="mt-3 max-w-3xl text-[length:var(--section-subtitle-size)] leading-relaxed text-muted-foreground">
          {description}
        </p>
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

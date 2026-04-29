import { createContext, useContext, type ReactNode } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { cn } from "../../lib/utils";

/**
 * Settings navigation organised around the main configuration decisions:
 * overview, library, quality, automation, and system behaviour.
 */
export const settingsNavGroups = [
  {
    label: "Overview",
    items: [
      { to: "/settings", label: "Settings Home", end: true, tip: "Configuration health, quick actions, and setup posture" }
    ]
  },
  {
    label: "Library",
    items: [
      { to: "/settings/media-management", label: "Media Management", end: false, tip: "Root folders, rename rules, hardlinks, and import behaviour" },
      { to: "/settings/destination-rules", label: "Destination Rules", end: false, tip: "Route titles into root folders based on tags, genres, languages, and more" },
      { to: "/settings/metadata", label: "Metadata", end: false, tip: "NFO files, artwork exports, and external media metadata output" },
      { to: "/settings/tags", label: "Tags", end: false, tip: "Organise titles for filtering, routing, and policy targeting" }
    ]
  },
  {
    label: "Quality",
    items: [
      { to: "/settings/policy-sets", label: "Policy Sets", end: false, tip: "Combine quality targets and destination rules into reusable acquisition policies" },
      { to: "/settings/profiles", label: "Profiles", end: false, tip: "Quality targets and upgrade policy per media policy set" },
      { to: "/settings/quality", label: "Size Rules", end: false, tip: "Minimum and maximum size boundaries for each quality tier" },
      { to: "/settings/custom-formats", label: "Custom Formats", end: false, tip: "Score releases by HDR, source, codec, language, and more" }
    ]
  },
  {
    label: "Automation",
    items: [
      { to: "/settings/lists", label: "Intake Sources", end: false, tip: "Bring titles in from Trakt, IMDb, or other external list sources" }
    ]
  },
  {
    label: "System",
    items: [
      { to: "/settings/general", label: "General", end: false, tip: "Instance identity, host settings, notifications, and startup behaviour" },
      { to: "/settings/ui", label: "Interface", end: false, tip: "Theme, density, default views, and experience preferences" }
    ]
  }
] as const;

/** Flat list kept for backwards compatibility. */
export const settingsNavItems = settingsNavGroups.flatMap((group) => [...group.items]);

const SettingsWorkspaceContext = createContext(false);
const SystemWorkspaceContext = createContext(false);

const systemNavItems = [
  { to: "/system", label: "Health", end: true, tip: "Runtime health, jobs, providers, and current posture" },
  { to: "/system/audit", label: "Audit", end: false, tip: "Searchable event timeline and live activity stream" },
  { to: "/system/api", label: "API", end: false, tip: "Generate and revoke API keys for integrations and automation" },
  { to: "/system/docs", label: "Guide", end: false, tip: "Plain-English workflow guide for setup, routing, scoring, imports, and integrations" },
  { to: "/system/backups", label: "Backups", end: false, tip: "Manual backups, automatic schedule, restore preview, and downloads" },
  { to: "/system/updates", label: "Updates", end: false, tip: "Version status, signed release checks, and upgrade readiness" }
] as const;

const settingsPageMeta = [
  {
    match: (path: string) => path === "/settings",
    title: "Settings overview",
    description:
      "Guided configuration for libraries, quality policy, automation, and runtime behaviour."
  },
  {
    match: (path: string) => path.startsWith("/settings/media-management"),
    title: "Media Management",
    description: "Naming, import, and file-handling behaviour for movies and TV."
  },
  {
    match: (path: string) => path.startsWith("/settings/destination-rules"),
    title: "Destination Rules",
    description: "Route media into the right root folders without running multiple Deluno instances."
  },
  {
    match: (path: string) => path.startsWith("/settings/policy-sets"),
    title: "Policy Sets",
    description: "Reusable acquisition policies that combine quality, routing, and automation decisions."
  },
  {
    match: (path: string) => path.startsWith("/settings/profiles"),
    title: "Profiles",
    description: "Quality targets, cutoffs, and upgrade behaviour for each library intent."
  },
  {
    match: (path: string) => path.startsWith("/settings/quality"),
    title: "Size Rules",
    description: "Size limits that keep downloads sane and predictable across qualities."
  },
  {
    match: (path: string) => path.startsWith("/settings/custom-formats"),
    title: "Custom Formats",
    description: "Release scoring for source, codec, HDR, language, group, and preference rules."
  },
  {
    match: (path: string) => path.startsWith("/settings/lists"),
    title: "Intake Sources",
    description: "External list sources and automated discovery behaviour."
  },
  {
    match: (path: string) => path.startsWith("/settings/metadata"),
    title: "Metadata",
    description: "NFO, artwork, certification, and library metadata output."
  },
  {
    match: (path: string) => path.startsWith("/settings/tags"),
    title: "Tags",
    description: "Labels used for filtering, routing, policies, and user organisation."
  },
  {
    match: (path: string) => path.startsWith("/settings/general"),
    title: "General",
    description: "Host identity, runtime defaults, startup behaviour, and notifications."
  },
  {
    match: (path: string) => path.startsWith("/settings/ui"),
    title: "Interface",
    description: "Theme, density, default views, and how Deluno should feel on your display."
  }
] as const;

const systemPageMeta = [
  {
    match: (path: string) => path === "/system",
    title: "System Health",
    description: "Runtime health, background jobs, provider posture, and operational state."
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
    description: "Version status, release channel, signed update checks, and upgrade readiness."
  }
] as const;

export function SettingsWorkspaceLayout() {
  const location = useLocation();
  const meta = settingsPageMeta.find((item) => item.match(location.pathname)) ?? settingsPageMeta[0];

  return (
    <SettingsShell title={meta.title} description={meta.description}>
      <SettingsWorkspaceContext.Provider value>
        <Outlet />
      </SettingsWorkspaceContext.Provider>
    </SettingsShell>
  );
}

export function SystemWorkspaceLayout() {
  const location = useLocation();
  const meta = systemPageMeta.find((item) => item.match(location.pathname)) ?? systemPageMeta[0];

  return (
    <SystemShell title={meta.title} description={meta.description}>
      <SystemWorkspaceContext.Provider value>
        <Outlet />
      </SystemWorkspaceContext.Provider>
    </SystemShell>
  );
}

export function SettingsShell({
  eyebrow = "Settings",
  title,
  description,
  children
}: {
  eyebrow?: string;
  title: string;
  description: string;
  children: ReactNode;
}) {
  if (useContext(SettingsWorkspaceContext)) {
    return <>{children}</>;
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      <div className="flex flex-col gap-2 xl:flex-row xl:items-end xl:justify-between xl:gap-[var(--grid-gap)]">
        <div className="min-w-0">
          <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground">
            {eyebrow}
          </p>
          <h1 className="mt-2 font-display text-[length:var(--type-title-lg)] font-semibold tracking-tight text-foreground">
            {title}
          </h1>
        </div>
        <p className="max-w-[min(58rem,100%)] text-[length:var(--section-subtitle-size)] leading-relaxed text-muted-foreground xl:text-right">
          {description}
        </p>
      </div>

      <div className="grid gap-[var(--grid-gap)] rounded-2xl border border-hairline/80 bg-card/80 p-[calc(var(--tile-pad)*0.8)] shadow-card dark:border-white/[0.07] dark:bg-white/[0.035] md:grid-cols-3">
        <SettingsStep step="A" title="Library" copy="Choose roots, naming, metadata, and destination rules first." />
        <SettingsStep step="B" title="Quality" copy="Set profiles, sizes, and custom formats after storage is clear." />
        <SettingsStep step="C" title="Automation" copy="Enable lists, schedules, and UI defaults once policy is correct." />
      </div>

      <SectionSubnav groups={settingsNavGroups} />

      <div className="min-w-0 space-y-[var(--page-gap)]">
        {children}
      </div>
    </div>
  );
}

function SettingsStep({ copy, step, title }: { copy: string; step: string; title: string }) {
  return (
    <div className="flex gap-3 rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.62)]">
      <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl border border-primary/25 bg-primary/10 font-mono text-sm font-bold text-primary">
        {step}
      </span>
      <span className="min-w-0">
        <span className="block font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
          {title}
        </span>
        <span className="mt-1 block text-[length:var(--section-subtitle-size)] leading-relaxed text-muted-foreground">
          {copy}
        </span>
      </span>
    </div>
  );
}

function SettingsNavLink({
  item,
  compact = false
}: {
  item: (typeof settingsNavItems)[number] | (typeof systemNavItems)[number];
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
  groups: typeof settingsNavGroups | readonly { label: string; items: readonly (typeof systemNavItems)[number][] }[];
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

export function SystemShell({
  title,
  description,
  children
}: {
  title: string;
  description: string;
  children: ReactNode;
}) {
  if (useContext(SystemWorkspaceContext)) {
    return <>{children}</>;
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      <div className="flex flex-col gap-2 xl:flex-row xl:items-end xl:justify-between xl:gap-[var(--grid-gap)]">
        <div>
          <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground">
            System
          </p>
          <h1 className="mt-2 font-display text-[length:var(--type-title-lg)] font-semibold tracking-tight text-foreground">
            {title}
          </h1>
        </div>
        <p className="max-w-[min(58rem,100%)] text-[length:var(--section-subtitle-size)] leading-relaxed text-muted-foreground xl:text-right">
          {description}
        </p>
      </div>

      <SectionSubnav groups={[{ label: "System", items: systemNavItems }]} />

      {children}
    </div>
  );
}

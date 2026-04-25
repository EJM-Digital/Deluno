import type { ReactNode } from "react";
import { NavLink } from "react-router-dom";
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
      { to: "/settings/quality", label: "Quality Sizes", end: false, tip: "Minimum and maximum size boundaries for each quality tier" },
      { to: "/settings/custom-formats", label: "Custom Formats", end: false, tip: "Score releases by HDR, source, codec, language, and more" }
    ]
  },
  {
    label: "Automation",
    items: [
      { to: "/settings/lists", label: "Lists", end: false, tip: "Bring titles in from Trakt, IMDb, or other external intake sources" }
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

      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(260px,0.28fr)_minmax(0,1fr)] 2xl:grid-cols-[minmax(300px,0.22fr)_minmax(0,1fr)]">
        <aside className="hidden xl:block">
          <nav className="sticky top-[calc(var(--content-pad-block)+104px)] overflow-hidden rounded-2xl border border-hairline/80 bg-card/88 p-3 shadow-card dark:border-white/[0.07] dark:bg-white/[0.035]">
            <span
              aria-hidden
              className="pointer-events-none absolute inset-x-5 top-0 h-px rounded-full"
              style={{ background: "linear-gradient(90deg, transparent, hsl(var(--primary)/0.45), hsl(var(--primary-2)/0.3), transparent)" }}
            />
            <div className="space-y-4">
              {settingsNavGroups.map((group) => (
                <div key={group.label}>
                  <p className="px-3 pb-2 text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
                    {group.label}
                  </p>
                  <div className="space-y-1">
                    {group.items.map((item) => (
                      <SettingsNavLink key={item.to} item={item} />
                    ))}
                  </div>
                </div>
              ))}
            </div>
          </nav>
        </aside>

        <div className="min-w-0 space-y-[var(--page-gap)]">
          <div className="no-scrollbar overflow-x-auto xl:hidden">
            <nav className="relative flex min-w-max items-center gap-1 overflow-hidden rounded-2xl border border-hairline/80 bg-card/85 p-2 shadow-card dark:border-white/[0.07] dark:bg-white/[0.035]">
              {settingsNavGroups.map((group, groupIndex) => (
                <div key={group.label} className="flex items-center gap-1">
                  {groupIndex > 0 ? <div className="mx-1.5 h-6 w-px bg-hairline/80" aria-hidden /> : null}
                  {group.items.map((item) => (
                    <SettingsNavLink key={item.to} item={item} compact />
                  ))}
                </div>
              ))}
            </nav>
          </div>

          {children}
        </div>
      </div>
    </div>
  );
}

function SettingsNavLink({
  item,
  compact = false
}: {
  item: (typeof settingsNavItems)[number];
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
          <span className="truncate">{item.label}</span>
        </>
      )}
    </NavLink>
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
  return (
    <div className="space-y-[var(--page-gap)]">
      <div className="space-y-2">
        <p className="text-[length:var(--section-subtitle-size)] text-muted-foreground">System</p>
        <h1 className="font-display text-[length:var(--type-title-lg)] font-semibold tracking-tight text-foreground">
          {title}
        </h1>
        <p className="max-w-3xl text-[length:var(--section-subtitle-size)] text-muted-foreground">{description}</p>
      </div>
      {children}
    </div>
  );
}

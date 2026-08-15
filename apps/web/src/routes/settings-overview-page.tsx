import { Link, useLoaderData } from "react-router-dom";
import {
  fetchJson,
  type DownloadClientItem,
  type IndexerItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type PolicySetItem,
  type QualityProfileItem
} from "../lib/api";
import { SettingsShell } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { RouteSkeleton } from "../components/shell/skeleton";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  downloadClients: DownloadClientItem[];
  indexers: IndexerItem[];
  policySets: PolicySetItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export async function settingsOverviewLoader(): Promise<SettingsOverviewLoaderData> {
  const [settings, libraries, qualityProfiles, policySets, indexers, downloadClients] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles"),
    fetchJson<PolicySetItem[]>("/api/policy-sets"),
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<DownloadClientItem[]>("/api/download-clients")
  ]);

  return { downloadClients, indexers, libraries, policySets, qualityProfiles, settings };
}

export function SettingsOverviewPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;

  const { downloadClients, indexers, libraries, policySets, qualityProfiles, settings } = loaderData;
  const enabledIndexers = indexers.filter((indexer) => indexer.isEnabled);
  const enabledClients = downloadClients.filter((client) => client.isEnabled);
  const healthyIndexers = enabledIndexers.filter((indexer) => indexer.healthStatus === "healthy");
  const healthyClients = enabledClients.filter((client) => client.healthStatus === "healthy");
  const activePlans = policySets.filter((plan) => plan.isEnabled).length;
  const movieLibraries = libraries.filter((library) => library.mediaType === "movies").length;
  const tvLibraries = libraries.filter((library) => library.mediaType === "tv").length;
  const autoLibraries = libraries.filter((library) => library.autoSearchEnabled).length;

  const nextSetupTask = libraries.length === 0
    ? { title: "Start with your library", copy: "Choose where movies and TV should live. Deluno needs a library route before it can safely import anything.", to: "/settings/media-management", action: "Set up library" }
    : enabledIndexers.length === 0 || enabledClients.length === 0
      ? { title: "Connect finding and downloading", copy: "Add at least one source and one download client before Deluno can acquire media automatically.", to: "/indexers", action: "Set up connections" }
      : activePlans === 0
        ? { title: "Choose media preferences", copy: "Select a Media Plan so Deluno knows what quality, size, releases, and upgrades you want.", to: "/settings/policy-sets", action: "Choose a Media Plan" }
        : { title: "Your essentials are ready", copy: "You can add media from the Dashboard. Import lists and automation preferences are optional refinements.", to: "/", action: "Open Dashboard" };

  return (
    <SettingsShell title="Settings overview" description="Set up your library once, then return here whenever you need to change how Deluno finds, processes, or stores media.">
      <section className="flex flex-col gap-[var(--grid-gap)] rounded-3xl border border-primary/20 bg-gradient-to-br from-primary/[0.08] via-card to-card p-[var(--tile-pad)] sm:flex-row sm:items-end sm:justify-between">
        <div>
          <p className="text-xs font-bold uppercase tracking-[0.14em] text-primary">Your media setup</p>
          <h2 className="mt-2 font-display text-2xl font-semibold tracking-tight text-foreground">{nextSetupTask.title}</h2>
          <p className="mt-2 max-w-3xl text-sm leading-relaxed text-muted-foreground">{nextSetupTask.copy}</p>
        </div>
        <Button asChild><Link to={nextSetupTask.to}>{nextSetupTask.action}</Link></Button>
      </section>

      <section aria-labelledby="setup-status-heading" className="overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]">
        <div className="border-b border-hairline px-[var(--tile-pad)] py-4">
          <h2 id="setup-status-heading" className="font-display text-lg font-semibold text-foreground">Setup status</h2>
          <p className="mt-1 text-sm text-muted-foreground">Open a section to configure it. These rows only show what still needs attention.</p>
        </div>
        <div className="divide-y divide-hairline">
          <SetupStatusRow label="Library" detail={libraries.length === 0 ? "No media folders configured" : `${movieLibraries} movie · ${tvLibraries} TV library`} to="/settings/media-management" action="Configure" />
          <SetupStatusRow label="Connections" detail={enabledIndexers.length === 0 && enabledClients.length === 0 ? "No indexers or download clients enabled" : `${healthyIndexers.length}/${enabledIndexers.length} indexers · ${healthyClients.length}/${enabledClients.length} download clients healthy`} to="/indexers" action="Configure" />
          <SetupStatusRow label="Media plans & quality" detail={activePlans > 0 ? `${activePlans} active Media Plan · ${qualityProfiles.length} quality profile${qualityProfiles.length === 1 ? "" : "s"}` : "No active Media Plan"} to="/settings/policy-sets" action="Configure" />
          <SetupStatusRow label="Automation & recovery" detail={settings.autoStartJobs ? `${autoLibraries} library automation setting${autoLibraries === 1 ? "" : "s"} active` : "Background automation is paused"} to="/settings/automation" action="Configure" />
        </div>
      </section>
    </SettingsShell>
  );
}

function SetupStatusRow({ label, detail, to, action }: { label: string; detail: string; to: string; action: string }) {
  return (
    <div className="flex flex-col gap-3 px-[var(--tile-pad)] py-4 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h3 className="font-semibold text-foreground">{label}</h3>
        <p className="mt-1 text-sm text-muted-foreground">{detail}</p>
      </div>
      <Button asChild size="sm" variant="ghost"><Link to={to}>{action}</Link></Button>
    </div>
  );
}

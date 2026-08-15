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

  const setupSteps = [
    {
      number: 1,
      title: "Library & storage",
      description: "Create your movie and TV libraries, choose their folders, then set the standard naming and import behaviour.",
      status: libraries.length === 0 ? "Not configured" : `${movieLibraries} movie · ${tvLibraries} TV library`,
      complete: libraries.length > 0,
      to: "/settings/libraries",
      action: libraries.length === 0 ? "Configure library" : "Review library"
    },
    {
      number: 2,
      title: "Connections",
      description: "Connect the indexers that find releases and the download clients that receive approved downloads.",
      status:
        enabledIndexers.length === 0 || enabledClients.length === 0
          ? "Indexers and download clients still need to be connected"
          : `${healthyIndexers.length}/${enabledIndexers.length} indexers · ${healthyClients.length}/${enabledClients.length} download clients healthy`,
      complete: enabledIndexers.length > 0 && enabledClients.length > 0,
      to: "/indexers",
      action: enabledIndexers.length === 0 || enabledClients.length === 0 ? "Configure connections" : "Review connections"
    },
    {
      number: 3,
      title: "Media plan & quality",
      description: "Choose the simple plan Deluno should follow for quality, size, releases and upgrades. Fine-tune profiles and scoring only if you need to.",
      status: activePlans > 0 ? `${activePlans} active Media Plan${activePlans === 1 ? "" : "s"} · ${qualityProfiles.length} quality profile${qualityProfiles.length === 1 ? "" : "s"}` : "No active Media Plan",
      complete: activePlans > 0,
      to: "/settings/policy-sets",
      action: activePlans > 0 ? "Review media plan" : "Choose media plan"
    },
    {
      number: 4,
      title: "Automation & recovery",
      description: "Decide when Deluno searches, upgrades, retries failed downloads and alerts you when a decision needs attention.",
      status: settings.autoStartJobs ? `${autoLibraries} library automation setting${autoLibraries === 1 ? "" : "s"} active` : "Background automation is paused",
      complete: settings.autoStartJobs,
      to: "/settings/automation",
      action: settings.autoStartJobs ? "Review automation" : "Configure automation"
    }
  ];
  const nextStep = setupSteps.find((step) => !step.complete) ?? null;

  return (
    <SettingsShell title="Setup overview" description="Set up Deluno once in this order. Advanced configuration is always available from the sidebar when you need it.">
      <section aria-labelledby="setup-status-heading" className="overflow-hidden rounded-2xl border border-hairline bg-card shadow-card dark:border-white/[0.07]">
        <div className="border-b border-hairline px-[var(--tile-pad)] py-4">
          <p className="text-xs font-bold uppercase tracking-[0.14em] text-primary">Your media setup</p>
          <h2 id="setup-status-heading" className="mt-1 font-display text-lg font-semibold text-foreground">Set up Deluno in order</h2>
          <p className="mt-1 text-sm text-muted-foreground">
            {nextStep ? `Start with step ${nextStep.number}: ${nextStep.title}.` : "Your core setup is complete. Use the sidebar whenever you want to refine a specific area."}
          </p>
        </div>
        <div className="divide-y divide-hairline">
          {setupSteps.map((step) => <SetupJourneyStep key={step.number} {...step} current={nextStep?.number === step.number} />)}
        </div>
      </section>
    </SettingsShell>
  );
}

function SetupJourneyStep({
  number,
  title,
  description,
  status,
  complete,
  current,
  to,
  action
}: {
  number: number;
  title: string;
  description: string;
  status: string;
  complete: boolean;
  current: boolean;
  to: string;
  action: string;
}) {
  return (
    <Link to={to} className="group flex gap-[var(--grid-gap)] px-[var(--tile-pad)] py-5 transition hover:bg-muted/35">
      <span className={complete ? "flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-success/15 text-sm font-bold text-success" : current ? "flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-bold text-primary-foreground" : "flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-muted text-sm font-bold text-muted-foreground"}>
        {complete ? "✓" : number}
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="font-semibold text-foreground">{title}</h3>
          {current ? <span className="rounded-full bg-primary/12 px-2 py-0.5 text-[11px] font-bold uppercase tracking-[0.1em] text-primary">Next</span> : null}
        </div>
        <p className="mt-1 max-w-3xl text-sm leading-relaxed text-muted-foreground">{description}</p>
        <p className={complete ? "mt-2 text-sm font-medium text-success" : "mt-2 text-sm font-medium text-muted-foreground"}>{status}</p>
      </div>
      <span className="self-center whitespace-nowrap rounded-lg px-3 py-2 text-sm font-semibold text-muted-foreground transition group-hover:bg-primary/12 group-hover:text-primary">{action} →</span>
    </Link>
  );
}

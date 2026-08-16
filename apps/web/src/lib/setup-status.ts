import type {
  DownloadClientItem,
  IndexerItem,
  LibraryItem,
  PlatformSettingsSnapshot,
  PolicySetItem,
  QualityProfileItem
} from "./api";

export type SetupAttentionTone = "success" | "warn" | "info" | "neutral";

export interface SetupStatusInput {
  libraries: LibraryItem[];
  downloadClients: DownloadClientItem[];
  indexers: IndexerItem[];
  policySets: PolicySetItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export interface SetupStatusStep {
  id: "library" | "connections" | "media-plans" | "automation";
  number: number;
  title: string;
  description: string;
  status: string;
  complete: boolean;
  to: string;
  action: string;
  attentionTitle: string;
  attentionText: string;
}

export interface SetupAttentionItem {
  id: string;
  title: string;
  text: string;
  href: string;
  action: string;
  tone: SetupAttentionTone;
}

export interface SetupStatusModel {
  steps: SetupStatusStep[];
  attentionItems: SetupAttentionItem[];
  completedCount: number;
  totalCount: number;
  isComplete: boolean;
  summary: string;
}

export function buildSetupStatus(input: SetupStatusInput): SetupStatusModel {
  const enabledIndexers = input.indexers.filter((indexer) => indexer.isEnabled);
  const enabledClients = input.downloadClients.filter((client) => client.isEnabled);
  const healthyIndexers = enabledIndexers.filter((indexer) => isHealthy(indexer.healthStatus));
  const healthyClients = enabledClients.filter((client) => isHealthy(client.healthStatus));
  const unhealthyIndexers = enabledIndexers.length - healthyIndexers.length;
  const unhealthyClients = enabledClients.length - healthyClients.length;
  const activePlans = input.policySets.filter((plan) => plan.isEnabled).length;
  const movieLibraries = input.libraries.filter((library) => library.mediaType === "movies").length;
  const tvLibraries = input.libraries.filter((library) => library.mediaType === "tv").length;
  const autoLibraries = input.libraries.filter((library) => library.autoSearchEnabled).length;

  const steps: SetupStatusStep[] = [
    {
      id: "library",
      number: 1,
      title: "Library & storage",
      description: "Create your movie and TV libraries, choose their folders, then set naming and import behaviour.",
      status:
        input.libraries.length === 0
          ? "Not configured"
          : `${plural(movieLibraries, "movie library")} - ${plural(tvLibraries, "TV library")}`,
      complete: input.libraries.length > 0,
      to: "/settings/libraries",
      action: input.libraries.length === 0 ? "Configure library" : "Review library",
      attentionTitle: "Library not configured",
      attentionText: "Create at least one movie or TV library and choose its final folder."
    },
    {
      id: "connections",
      number: 2,
      title: "Connections",
      description: "Connect the search sources that find releases and the download clients that receive approved downloads.",
      status:
        enabledIndexers.length === 0 || enabledClients.length === 0
          ? missingConnectionStatus(enabledIndexers.length, enabledClients.length)
          : `${healthyIndexers.length}/${enabledIndexers.length} search sources healthy - ${healthyClients.length}/${enabledClients.length} download clients healthy`,
      complete: enabledIndexers.length > 0 && enabledClients.length > 0,
      to: "/indexers",
      action: enabledIndexers.length === 0 || enabledClients.length === 0 ? "Configure connections" : "Review connections",
      attentionTitle: "Connections incomplete",
      attentionText: missingConnectionDetail(enabledIndexers.length, enabledClients.length)
    },
    {
      id: "media-plans",
      number: 3,
      title: "Media Plans",
      description: "Choose the Media Plan Deluno follows for quality, size, releases, and upgrades.",
      status:
        activePlans > 0
          ? `${plural(activePlans, "active Media Plan")} - ${plural(input.qualityProfiles.length, "quality profile")}`
          : "No active Media Plan",
      complete: activePlans > 0,
      to: "/settings/policy-sets",
      action: activePlans > 0 ? "Review Media Plans" : "Choose Media Plan",
      attentionTitle: "No Media Plan selected",
      attentionText: "Choose a default Media Plan so Deluno knows the quality, size, release, and upgrade rules to follow."
    },
    {
      id: "automation",
      number: 4,
      title: "Automation & recovery",
      description: "Decide when Deluno searches, upgrades, retries failed downloads, and alerts you when decisions need attention.",
      status: input.settings.autoStartJobs
        ? `${plural(autoLibraries, "library automation setting")} active`
        : "Background automation is paused",
      complete: input.settings.autoStartJobs,
      to: "/settings/automation",
      action: input.settings.autoStartJobs ? "Review automation" : "Configure automation",
      attentionTitle: "Automation paused",
      attentionText: "Turn background automation on when you want scheduled searches, upgrades, retries, and recovery checks to run."
    }
  ];

  const attentionItems: SetupAttentionItem[] = steps
    .filter((step) => !step.complete)
    .map((step) => ({
      id: step.id,
      title: step.attentionTitle,
      text: step.attentionText,
      href: step.to,
      action: step.action,
      tone: "warn"
    }));

  if (enabledIndexers.length > 0 && enabledClients.length > 0 && unhealthyIndexers + unhealthyClients > 0) {
    attentionItems.push({
      id: "connection-health",
      title: "Connection health needs review",
      text: `${formatHealthParts(unhealthyIndexers, unhealthyClients)} not reporting healthy.`,
      href: "/indexers",
      action: "Review connections",
      tone: "warn"
    });
  }

  const nextStep = steps.find((step) => !step.complete) ?? null;
  const completedCount = steps.filter((step) => step.complete).length;

  return {
    steps,
    attentionItems,
    completedCount,
    totalCount: steps.length,
    isComplete: completedCount === steps.length,
    summary: nextStep
      ? `Start with step ${nextStep.number}: ${nextStep.title}.`
      : attentionItems.length > 0
        ? "Core setup is complete. Review connection health before relying on automation."
        : "Core setup complete. No setup items need attention."
  };
}

function missingConnectionStatus(enabledIndexers: number, enabledClients: number) {
  if (enabledIndexers === 0 && enabledClients === 0) return "Search sources and download clients still need to be connected";
  if (enabledIndexers === 0) return "Search sources still need to be connected";
  return "Download clients still need to be connected";
}

function missingConnectionDetail(enabledIndexers: number, enabledClients: number) {
  if (enabledIndexers === 0 && enabledClients === 0) {
    return "Add at least one search source and one download client before Deluno can find and send releases.";
  }
  if (enabledIndexers === 0) return "Add at least one enabled search source so Deluno can find releases.";
  return "Add at least one enabled download client so Deluno can send approved releases.";
}

function formatHealthParts(unhealthyIndexers: number, unhealthyClients: number) {
  const parts = [
    unhealthyIndexers > 0 ? plural(unhealthyIndexers, "search source") : null,
    unhealthyClients > 0 ? plural(unhealthyClients, "download client") : null
  ].filter((part): part is string => Boolean(part));

  return parts.length === 2 ? `${parts[0]} and ${parts[1]}` : parts[0] ?? "Connections";
}

function plural(count: number, singular: string, pluralLabel = `${singular}s`) {
  return `${count} ${count === 1 ? singular : pluralLabel}`;
}

function isHealthy(status: string) {
  return status === "healthy";
}

import type {
  DownloadClientItem,
  IndexerItem,
  IntakeSourceItem,
  LibraryItem,
  PlatformSettingsSnapshot,
  PolicySetItem,
  QualityProfileItem
} from "./api";

export type SetupAttentionTone = "success" | "warn" | "info" | "neutral";
export type SetupReadiness = "not-ready" | "acquisition-ready" | "automation-ready" | "operationally-ready";
export type SetupStepState = "not-started" | "failed" | "complete";

export interface SetupStatusInput {
  libraries: LibraryItem[];
  downloadClients: DownloadClientItem[];
  indexers: IndexerItem[];
  intakeSources?: IntakeSourceItem[];
  policySets: PolicySetItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export interface SetupStatusStep {
  id: "library" | "media-plans" | "connections" | "automation" | "workflow" | "discovery";
  number: number;
  title: string;
  description: string;
  status: string;
  complete: boolean;
  state: SetupStepState;
  optional: boolean;
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
  optionalConfiguredCount: number;
  isComplete: boolean;
  readiness: SetupReadiness;
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
  const enabledIntakeSources = (input.intakeSources ?? []).filter((source) => source.isEnabled);
  const hasHealthyIndexer = healthyIndexers.length > 0;
  const hasHealthyClient = healthyClients.length > 0;
  const connectionsReady = hasHealthyIndexer && hasHealthyClient;
  const automationReady = input.settings.autoStartJobs && autoLibraries > 0;
  const readyLibraries = input.libraries.filter((library) => isLibraryReady(library, input.settings));
  const mediaManagementReady = input.libraries.length > 0 && readyLibraries.length === input.libraries.length;

  const steps: SetupStatusStep[] = [
    {
      id: "library",
      number: 1,
      title: "Media Management",
      description: "Set up your movie and TV libraries, storage paths, naming, and import behaviour.",
      status:
        input.libraries.length === 0
          ? "Not configured"
          : mediaManagementReady
            ? `${plural(movieLibraries, "movie library")} - ${plural(tvLibraries, "TV library")}`
            : `${readyLibraries.length}/${input.libraries.length} libraries ready`,
      complete: mediaManagementReady,
      state: mediaManagementReady ? "complete" : input.libraries.length > 0 ? "failed" : "not-started",
      optional: false,
      to: "/settings/libraries",
      action: mediaManagementReady
        ? "Review media management"
        : input.libraries.length > 0
          ? "Finish media management"
          : "Configure media management",
      attentionTitle: "Media management not configured",
      attentionText: input.libraries.length === 0
        ? "Create at least one movie or TV library and choose its final folder."
        : "Finish every library's destination, naming, and import workflow before treating media management as ready."
    },
    {
      id: "media-plans",
      number: 2,
      title: "Library Profiles",
      description: "Choose the quality, size, release, and upgrade behaviour Deluno will follow.",
      status: activePlans > 0 ? `${plural(activePlans, "active library profile")} - ${plural(input.qualityProfiles.length, "quality profile")}` : "No library profile selected",
      complete: activePlans > 0,
      state: activePlans > 0 ? "complete" : "not-started",
      optional: false,
      to: "/settings/policy-sets",
      action: activePlans > 0 ? "Review Library Profiles" : "Choose Library Profiles",
      attentionTitle: "No library profile selected",
      attentionText: "Choose a Library Profile so Deluno knows which releases to accept, hold, reject, and upgrade."
    },
    {
      id: "connections",
      number: 3,
      title: "Find & Download",
      description: "Connect and test the search sources that find releases and the download clients that receive approved work.",
      status: connectionStatus(enabledIndexers.length, healthyIndexers.length, enabledClients.length, healthyClients.length),
      complete: connectionsReady,
      state: connectionsReady ? "complete" : connectionStepState(enabledIndexers.length, healthyIndexers.length, enabledClients.length, healthyClients.length),
      optional: false,
      to: "/indexers",
      action: connectionsReady ? "Review connections" : "Configure connections",
      attentionTitle: "Acquisition connections not ready",
      attentionText: missingConnectionDetail(healthyIndexers.length, healthyClients.length)
    },
    {
      id: "automation",
      number: 4,
      title: "Automation & Recovery",
      description: "Decide when Deluno searches, upgrades, retries failed downloads, and alerts you when decisions need attention.",
      status: automationStatus(input.settings.autoStartJobs, autoLibraries),
      complete: automationReady,
      state: automationReady ? "complete" : "not-started",
      optional: false,
      to: "/settings/automation",
      action: automationReady ? "Review automation" : "Configure automation",
      attentionTitle: "Automation is not ready",
      attentionText: automationAttentionText(input.settings.autoStartJobs, autoLibraries)
    },
    {
      id: "discovery",
      number: 5,
      title: "Discover Media",
      description: "Optionally configure import lists or watchlists with provenance, exclusions, and reviewable sync results.",
      status: enabledIntakeSources.length > 0 ? `${plural(enabledIntakeSources.length, "import list")} enabled` : "Optional - not configured",
      complete: enabledIntakeSources.length > 0,
      state: enabledIntakeSources.length > 0 ? "complete" : "not-started",
      optional: true,
      to: "/settings/lists",
      action: enabledIntakeSources.length > 0 ? "Review import lists" : "Configure import lists",
      attentionTitle: "Import lists are optional",
      attentionText: "Add import lists only if you want Deluno to discover titles for you. Manual title entry remains available."
    },
    {
      id: "workflow",
      number: 6,
      title: "First Acquisition",
      description: "Run one complete search, dispatch, download, import, and catalogue flow before calling setup operationally ready.",
      status: input.settings.workflowVerified ? "End-to-end acquisition verified" : "First end-to-end acquisition not verified",
      complete: input.settings.workflowVerified,
      state: input.settings.workflowVerified ? "complete" : "not-started",
      optional: false,
      to: "/movies",
      action: input.settings.workflowVerified ? "Review first flow" : "Run first acquisition",
      attentionTitle: "First workflow not verified",
      attentionText: "Add or choose a title, dispatch a release, and verify that the completed download imports into the library."
    }
  ];

  const requiredSteps = steps.filter((step) => !step.optional);
  const completedCount = requiredSteps.filter((step) => step.complete).length;
  const totalCount = requiredSteps.length;
  const optionalConfiguredCount = steps.filter((step) => step.optional && step.complete).length;
  const attentionItems: SetupAttentionItem[] = requiredSteps
    .filter((step) => !step.complete)
    .map((step) => ({
      id: step.id,
      title: step.attentionTitle,
      text: step.attentionText,
      href: step.to,
      action: step.action,
      tone: "warn"
    }));

  if (connectionsReady && unhealthyIndexers + unhealthyClients > 0) {
    attentionItems.push({
      id: "connection-health",
      title: "Connection health needs review",
      text: `${formatHealthParts(unhealthyIndexers, unhealthyClients)} not reporting healthy.`,
      href: "/indexers",
      action: "Review connections",
      tone: "warn"
    });
  }

  const nextStep = requiredSteps.find((step) => !step.complete) ?? null;
  const isComplete = completedCount === totalCount;
  const readiness: SetupReadiness = !steps[0].complete || !steps[1].complete || !connectionsReady
    ? "not-ready"
    : !automationReady
      ? "acquisition-ready"
      : !input.settings.workflowVerified
        ? "automation-ready"
        : "operationally-ready";

  return {
    steps,
    attentionItems,
    completedCount,
    totalCount,
    optionalConfiguredCount,
    isComplete,
    readiness,
    summary: nextStep
      ? `Start with step ${nextStep.number}: ${nextStep.title}.`
      : optionalConfiguredCount > 0
        ? "Operational setup complete. Your import lists are also configured."
        : "Operational setup complete. Import lists are optional and can be added later."
  };
}

function connectionStatus(enabledIndexers: number, healthyIndexers: number, enabledClients: number, healthyClients: number) {
  if (enabledIndexers === 0 && enabledClients === 0) return "Search sources and download clients still need to be connected";
  if (healthyIndexers === 0 && healthyClients === 0) return "Search sources and download clients need a successful connection test";
  if (healthyIndexers === 0) return "A healthy search source still needs to be connected";
  if (healthyClients === 0) return "A healthy download client still needs to be connected";
  return `${healthyIndexers}/${enabledIndexers} search sources healthy - ${healthyClients}/${enabledClients} download clients healthy`;
}

function connectionStepState(enabledIndexers: number, healthyIndexers: number, enabledClients: number, healthyClients: number): SetupStepState {
  const attemptedIndexer = enabledIndexers > 0;
  const attemptedClient = enabledClients > 0;
  const failedIndexer = attemptedIndexer && healthyIndexers === 0;
  const failedClient = attemptedClient && healthyClients === 0;
  return failedIndexer || failedClient ? "failed" : "not-started";
}

function missingConnectionDetail(healthyIndexers: number, healthyClients: number) {
  if (healthyIndexers === 0 && healthyClients === 0) {
    return "Add and test at least one search source and one download client before Deluno can find and send releases.";
  }
  if (healthyIndexers === 0) return "Add and test at least one enabled search source so Deluno can find releases.";
  return "Add and test at least one enabled download client so Deluno can send approved releases.";
}

function automationStatus(autoStartJobs: boolean, autoLibraries: number) {
  if (!autoStartJobs) return "Background automation is paused";
  if (autoLibraries === 0) return "No library automation is enabled";
  return `${plural(autoLibraries, "library automation setting")} active`;
}

function automationAttentionText(autoStartJobs: boolean, autoLibraries: number) {
  if (!autoStartJobs) return "Turn background automation on when you want scheduled searches, upgrades, retries, and recovery checks to run.";
  return autoLibraries === 0
    ? "Enable missing or upgrade searches on at least one library so Deluno can operate the configured acquisition plan."
    : "Review the automation and recovery settings before relying on scheduled acquisition.";
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

function isLibraryReady(library: LibraryItem, settings: PlatformSettingsSnapshot) {
  if (!library.rootPath?.trim()) return false;

  const mediaType = library.mediaType?.toLowerCase();
  const namingReady = mediaType === "movies"
    ? Boolean(settings.movieFolderFormat?.trim())
    : mediaType === "tv"
      ? Boolean(settings.seriesFolderFormat?.trim()) && Boolean(settings.episodeFileFormat?.trim())
      : false;
  if (!namingReady) return false;

  const workflow = (library.importWorkflow ?? "standard").trim().toLowerCase();
  return workflow !== "refine-before-import" || Boolean(library.processorOutputPath?.trim());
}

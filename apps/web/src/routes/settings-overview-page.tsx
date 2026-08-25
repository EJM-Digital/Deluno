import { Link, useLoaderData } from "react-router-dom";
import { CheckCircle2 } from "lucide-react";
import {
  fetchJson,
  type DownloadClientItem,
  type IndexerItem,
  type IntakeSourceItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type PolicySetItem,
  type QualityProfileItem
} from "../lib/api";
import { buildSetupStatus, type SetupStatusStep } from "../lib/setup-status";
import { SettingsShell } from "../components/app/settings-shell";
import { Card } from "../components/ui/card";

interface SettingsOverviewLoaderData {
  libraries: LibraryItem[];
  downloadClients: DownloadClientItem[];
  indexers: IndexerItem[];
  intakeSources: IntakeSourceItem[];
  policySets: PolicySetItem[];
  qualityProfiles: QualityProfileItem[];
  settings: PlatformSettingsSnapshot;
}

export async function settingsOverviewLoader(): Promise<SettingsOverviewLoaderData> {
  const [settings, libraries, qualityProfiles, policySets, indexers, downloadClients, intakeSources] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles"),
    fetchJson<PolicySetItem[]>("/api/policy-sets"),
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<DownloadClientItem[]>("/api/download-clients"),
    fetchJson<IntakeSourceItem[]>("/api/intake-sources")
  ]);

  return { downloadClients, indexers, intakeSources, libraries, policySets, qualityProfiles, settings };
}

export function SettingsOverviewPage() {
  const loaderData = useLoaderData() as SettingsOverviewLoaderData;

  const { downloadClients, indexers, intakeSources, libraries, policySets, qualityProfiles, settings } = loaderData;
  const setupStatus = buildSetupStatus({ downloadClients, indexers, intakeSources, libraries, policySets, qualityProfiles, settings });
  const setupSteps = setupStatus.steps;
  const nextStep = setupSteps.find((step) => !step.optional && !step.complete) ?? null;

  return (
    <SettingsShell description="Follow the complete acquisition workflow in order. Detailed configuration remains available from the owning pages.">
      <Card as="section" aria-labelledby="setup-status-heading" className="dark:border-white/[0.07]">
        <div className="flex flex-col gap-3 border-b border-hairline px-[var(--tile-pad)] py-4 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <p className="text-xs font-bold uppercase tracking-[0.14em] text-primary">Your media setup</p>
            <h2 id="setup-status-heading" className="mt-1 font-display text-lg font-semibold text-foreground">Set up Deluno in order</h2>
            <p className="mt-1 text-sm text-muted-foreground">{setupStatus.summary}</p>
            <p className="mt-2 text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">Readiness: {readinessLabel(setupStatus.readiness)}</p>
          </div>
          <span className="inline-flex h-9 items-center rounded-full border border-hairline bg-surface-1 px-3 text-sm font-semibold text-muted-foreground">
            {setupStatus.completedCount}/{setupStatus.totalCount} required steps complete
          </span>
        </div>
        <div className="divide-y divide-hairline">
          {setupSteps.map((step) => <SetupJourneyStep key={step.number} {...step} current={nextStep?.number === step.number} />)}
        </div>
      </Card>
    </SettingsShell>
  );
}

function SetupJourneyStep({
  number,
  title,
  description,
  status,
  complete,
  optional,
  current,
  to,
  action
}: {
  number: SetupStatusStep["number"];
  title: SetupStatusStep["title"];
  description: SetupStatusStep["description"];
  status: SetupStatusStep["status"];
  complete: SetupStatusStep["complete"];
  optional: SetupStatusStep["optional"];
  current: boolean;
  to: SetupStatusStep["to"];
  action: SetupStatusStep["action"];
}) {
  return (
    <Link to={to} className="group flex gap-[var(--grid-gap)] px-[var(--tile-pad)] py-5 transition hover:bg-muted/35">
      <span
        aria-label={optional ? `${title} optional${complete ? " configured" : ""}` : complete ? `${title} complete` : current ? `${title} next` : `${title} incomplete`}
        className={complete ? "flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-success/15 text-sm font-bold text-success" : current ? "flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-bold text-primary-foreground" : "flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-muted text-sm font-bold text-muted-foreground"}
      >
        {complete ? <CheckCircle2 className="h-4 w-4" aria-hidden="true" /> : number}
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="font-semibold text-foreground">{title}</h3>
          {optional ? <span className="inline-flex items-center gap-1 rounded-full bg-muted px-2 py-0.5 text-[length:var(--type-caption)] font-bold uppercase tracking-[0.1em] text-muted-foreground">{complete ? "Configured" : "Optional"}</span> : complete ? <span className="inline-flex items-center gap-1 rounded-full bg-success/12 px-2 py-0.5 text-[length:var(--type-caption)] font-bold uppercase tracking-[0.1em] text-success">Done</span> : null}
          {!optional && current ? <span className="rounded-full bg-primary/12 px-2 py-0.5 text-[length:var(--type-caption)] font-bold uppercase tracking-[0.1em] text-primary">Next</span> : null}
        </div>
        <p className="mt-1 max-w-3xl text-sm leading-relaxed text-muted-foreground">{description}</p>
        <p className={complete && !optional ? "mt-2 text-sm font-medium text-success" : "mt-2 text-sm font-medium text-muted-foreground"}>{status}</p>
      </div>
      <span className="self-center whitespace-nowrap rounded-lg px-3 py-2 text-sm font-semibold text-muted-foreground transition group-hover:bg-primary/12 group-hover:text-primary">{action} →</span>
    </Link>
  );
}

function readinessLabel(readiness: ReturnType<typeof buildSetupStatus>["readiness"]) {
  switch (readiness) {
    case "acquisition-ready":
      return "Acquisition ready";
    case "automation-ready":
      return "Automation ready; first flow still required";
    case "operationally-ready":
      return "Operationally ready";
    default:
      return "Not ready";
  }
}

/**
 * Dashboard, Setup health (#1087): the plumbing.
 *
 * The download clients (Resume, Test again), the indexers (Test again), the
 * processor, storage per drive, automation per library, the scheduled tasks and
 * the system (version, updates, backups, this computer), under one strip. The
 * connection, storage and system problems are listed here with their buttons,
 * and the Setup health badge counts exactly those.
 *
 * Each fact appears once: the strip says how Deluno is doing overall, and the
 * panels beneath say what is behind it.
 */
import { useMemo } from "react";
import { useRouteSources } from "../../lib/route-cache";
import type { DashboardSummary } from "../../lib/api";
import { attentionSources } from "./attention";
import { Clients, Indexers, ProcessorPanel } from "./downloads-panels";
import { useAction } from "./downloads-kit";
import { Automation, Machine, Problems, ScheduledTasks, SetupStrip, Storage, UpdatesAndBackups, setupPanelSources } from "./system-panels";
import { useSetupState } from "./sources";
import { useDashboardAttention } from "./use-dashboard-attention";

export function SetupTab({ summary }: { summary: DashboardSummary | undefined }) {
  const { data } = useRouteSources({ ...attentionSources, ...setupPanelSources });
  const setup = useSetupState();
  const action = useAction();
  const attention = useDashboardAttention(summary, setup.needs);
  const processors = useMemo(() => (data.processors ?? []).filter((processor) => processor.isEnabled), [data.processors]);

  return (
    <div className="flex h-full min-h-0 flex-col gap-3">
      <SetupStrip summary={summary} problems={attention.counts.setup} jobsGaveUp={attention.jobsGaveUp} />
      <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 lg:grid-cols-3">
        <div className="flex min-h-0 flex-col gap-3">
          <Problems summary={summary} attention={attention} action={action} />
          <Clients summary={summary} action={action} />
          <ProcessorPanel summary={summary} processors={processors} handoffs={data.handoffs} action={action} />
        </div>
        <div className="flex min-h-0 flex-col gap-3">
          <Indexers summary={summary} indexers={data.indexers} action={action} className="flex-[3]" />
          <Automation summary={summary} />
          <ScheduledTasks summary={summary} />
        </div>
        <div className="flex min-h-0 flex-col gap-3">
          <Storage summary={summary} />
          <Machine summary={summary} window={data.machine} />
          <UpdatesAndBackups summary={summary} />
        </div>
      </div>
    </div>
  );
}

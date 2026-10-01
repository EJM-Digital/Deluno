/**
 * Dashboard, System (#1087): how Deluno and the machine it runs on are doing.
 *
 * Every health check with its state and the button that clears each problem;
 * the download clients, indexers and processor with theirs; this computer
 * (processor, memory, drive load, Deluno's own disk use, API response times);
 * storage per drive; automation per library; the scheduled tasks; updates and
 * backups. The System badge counts exactly the problems listed here.
 *
 * Each fact appears once: the strip says how Deluno is doing overall, and the
 * panels beneath say what is behind it.
 */
import { useMemo } from "react";
import { useRouteSources } from "../../lib/route-cache";
import type { DashboardSummary } from "../../lib/api";
import { attentionSources } from "./attention";
import { Connections } from "./connections-panel";
import { useAction } from "./downloads-kit";
import { Automation, Health, LibraryCounts, Machine, ScheduledTasks, Storage, SystemStrip, UpdatesAndBackups, systemPanelSources } from "./system-panels";
import { useSetupState } from "./sources";
import { useDashboardAttention } from "./use-dashboard-attention";

export function SystemTab({ summary }: { summary: DashboardSummary | undefined }) {
  const { data } = useRouteSources({ ...attentionSources, ...systemPanelSources });
  const setup = useSetupState();
  const action = useAction();
  const attention = useDashboardAttention(summary, setup.needs);
  const processors = useMemo(() => (data.processors ?? []).filter((processor) => processor.isEnabled), [data.processors]);
  const checks = data.healthChecks?.checks;

  return (
    <div className="flex h-full min-h-0 flex-col gap-3">
      <SystemStrip summary={summary} problems={attention.counts.system} jobsGaveUp={attention.jobsGaveUp} checks={checks} monitoring={data.monitoring} />
      <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 lg:grid-cols-3">
        <div className="flex min-h-0 flex-col gap-3">
          <Health summary={summary} attention={attention} checks={checks} action={action} className="flex-[3]" />
          <Connections summary={summary} indexers={data.indexers} processors={processors} action={action} className="flex-[2]" />
        </div>
        <div className="flex min-h-0 flex-col gap-3">
          <Machine summary={summary} window={data.machine} monitoring={data.monitoring} />
          <Storage summary={summary} extras={data.liveExtras} />
          <UpdatesAndBackups summary={summary} />
        </div>
        <div className="flex min-h-0 flex-col gap-3">
          <LibraryCounts summary={summary} />
          <Automation summary={summary} />
          <ScheduledTasks summary={summary} />
        </div>
      </div>
    </div>
  );
}

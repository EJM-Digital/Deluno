/**
 * Dashboard, System (#1087, #1098): how Deluno and the machine it runs on are doing, as one grid with Live's (the same
 * row heights and column widths, so no box moves when you switch tabs): along the top This instance, This computer and
 * Network with Storage in the top-right slot; Health across the left with Connections beside it; and at the bottom
 * Scheduled tasks, the Log and Backups and databases.
 *
 * Each fact appears once: the ring on This instance says how many checks pass and links to Health, which holds the
 * problems; Connections holds the answers of everything Deluno talks to; the library is on Live.
 */
import { useMemo, useState, type JSX } from "react";
import { useRouteSources } from "../../lib/route-cache";
import type { DashboardSummary } from "../../lib/api";
import { cn } from "../../lib/utils";
import { storedLists } from "./sources";
import { useAttentionSummary } from "../../lib/attention-summary";
import { GRID_AREAS_SYSTEM, GRID_COLUMNS, gridRows, lowNeedOfGrid, pageLayout } from "./live/layout";
import { useElementSize, useNow, useRemPx } from "./live/hooks";
import { ComputerCard, InstanceCard, NetworkCard } from "./system/band";
import { BackupsCard, LogCard, TasksCard } from "./system/bottom";
import { ConnectionsCard, useConnectionTesting } from "./system/connections-card";
import { buildConnections } from "./system/connections-model";
import { systemSources, useSystemDatabases, useSystemLog, useSystemOverview } from "./system/data";
import { HealthCard } from "./system/health-card";
import { buildHealth } from "./system/problems";
import { StorageCard, drivesOf } from "./system/storage-card";
import { useResizing } from "./system/use-resizing";
import "./system/system.css";

const NO_ROWS: never[] = [];

export function SystemTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  const { data, failed: failedSources } = useRouteSources({ ...storedLists, ...systemSources });
  const attention = useAttentionSummary();
  const overview = useSystemOverview();
  const log = useSystemLog();
  const { databases, failed: databasesFailed } = useSystemDatabases();
  const now = useNow(15_000);
  const testing = useConnectionTesting();
  const [jump, setJump] = useState(0);

  const checks = data.healthChecks?.checks;
  const health = useMemo(() => buildHealth({ summary, attention, checks }), [summary, attention, checks]);
  const connections = useMemo(
    () => buildConnections({ indexers: data.indexers ?? NO_ROWS, clients: data.clients ?? NO_ROWS, clientRows: summary?.downloads?.clients ?? NO_ROWS, connections: data.liveExtras?.connections, metadataMs: overview?.network?.metadataMs ?? null, results: testing.results, now }),
    [data.indexers, data.clients, summary?.downloads?.clients, data.liveExtras?.connections, overview?.network?.metadataMs, testing.results, now]
  );
  const drives = useMemo(() => drivesOf(overview?.drives, summary), [overview?.drives, summary]);

  const [pageRef, page] = useElementSize<HTMLDivElement>();
  const rem = useRemPx();
  const side = pageLayout(page.width, rem).sideBySide;
  // The one shared grid, from the same rule as Live's (see live/layout.ts), re-read whenever the grid is resized.
  const [gridRef, grid] = useElementSize<HTMLDivElement>();
  const windowHeight = typeof window === "undefined" ? 0 : window.innerHeight;
  // Live's shelf is not on this tab, so the same need is worked out from the grid's width: Live and System always agree.
  const rows = gridRows(grid.height > 0 ? windowHeight : 0, grid.height, { lowNeed: grid.width > 0 ? lowNeedOfGrid(grid.width) : undefined });
  const resizing = useResizing();

  return (
    <div ref={pageRef} className={cn("sy flex flex-col", side && "h-full min-h-0", resizing && "sy-resizing", side && rows.band < 180 && "sy-short")}>
      <div
        ref={gridRef}
        data-live-grid={side ? "shared" : "stacked"}
        className={cn("sy-grid", side ? "min-h-0 flex-1" : "sy-stacked")}
        style={side ? { gridTemplateRows: rows.template, gridTemplateAreas: GRID_AREAS_SYSTEM, gridTemplateColumns: GRID_COLUMNS } : undefined}
      >
        <div className="sy-band">
          <InstanceCard instance={overview?.instance ?? null} summary={summary} short={side && rows.band < 180} health={{ pass: health.pass, total: health.total, need: health.need, anyBad: health.tallies.some((tally) => tally.tone === "bad") }} onJump={() => setJump((n) => n + 1)} />
          <ComputerCard overview={overview} />
          <NetworkCard overview={overview} />
        </div>
        <StorageCard drives={drives} />
        <div className="sy-mid">
          <HealthCard model={health} hasChecks={Boolean(checks)} jump={jump} now={now} />
        </div>
        <ConnectionsCard rows={connections} testing={testing} now={now} />
        <div className="sy-low">
          <TasksCard tasks={summary?.automation?.tasks ?? NO_ROWS} />
          <LogCard log={log} />
        </div>
        <BackupsCard backups={data.backups ?? NO_ROWS} settings={data.backupSettings} databases={databases} backupsFailed={failedSources.some((source) => source.name === "backups")} databasesFailed={databasesFailed} />
      </div>
    </div>
  );
}

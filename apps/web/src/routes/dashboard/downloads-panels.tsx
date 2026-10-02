/**
 * Downloads now (#1087): what the download clients are working on this moment,
 * with Search again for a stalled download. Read from the stored download queue
 * the other screens cache; nothing here reaches a client while the screen waits.
 */
import { Chip } from "../../components/ui/chip";
import { CountUp } from "../../components/ui/count-up";
import { ListCell, ListNameCell, ListRow, ListTable } from "../../components/ui/list-card";
import type { DownloadQueueItem, DownloadTelemetryOverview } from "../../lib/api";
import type { DashboardSummary } from "../../lib/api/types/operations";
import { submitQueueRemoval } from "../../lib/remove-download";
import { cn } from "../../lib/utils";
import { ActionButton, Panel, PanelNote, formatEta, formatSpeed, plural, useAction } from "./downloads-kit";
import { FitList } from "./parts";
import type { DashboardAttention } from "./attention";
import type { LiveProgress } from "./live-events";

type Action = ReturnType<typeof useAction>;

/* ------------------------------------------------------- downloads now */

const WORKING = ["downloading", "stalled", "queued", "notTransferring", "paused"];

/**
 * The download queue as the clients last reported it, with any reading the
 * server has pushed since laid over it. A pushed reading is only trusted over
 * the stored queue while it is newer than the queue, so a download that has
 * since finished never shows an old figure.
 */
function nowRows(telemetry: DownloadTelemetryOverview | undefined, live: ReadonlyMap<string, LiveProgress>): DownloadQueueItem[] {
  const rank = (status: string) => WORKING.indexOf(status);
  const storedAt = Date.parse(telemetry?.capturedUtc ?? "");
  return (telemetry?.clients ?? [])
    .flatMap((client) => client.queue)
    .map((item) => {
      const reading = live.get(item.id);
      if (!reading || !(reading.at > (Number.isFinite(storedAt) ? storedAt : 0))) return item;
      return { ...item, progress: reading.progress, speedMbps: reading.speedMbps, status: WORKING.includes(reading.status) ? reading.status : item.status };
    })
    .filter((item) => WORKING.includes(item.status))
    .sort((a, b) => rank(a.status) - rank(b.status) || b.progress - a.progress);
}

/** Progress arrives as 0 to 100. */
function percent(progress: number): number {
  return Number.isFinite(progress) ? Math.min(100, Math.max(0, Math.round(progress))) : 0;
}

const NO_READINGS: ReadonlyMap<string, LiveProgress> = new Map();

export function DownloadsNow({ summary, telemetry, attention, action, live = NO_READINGS }: { summary: DashboardSummary | undefined; telemetry: DownloadTelemetryOverview | undefined; attention: DashboardAttention; action: Action; live?: ReadonlyMap<string, LiveProgress> }) {
  // A stalled download that an import case is about is that case, which has the buttons.
  const stalledWithButton = new Set(attention.downloads.stalled.map((item) => `${item.clientId}:${item.id}`));
  const rows = nowRows(telemetry, live);
  const downloads = summary?.downloads;
  const imported = summary?.pipeline?.today.imported ?? 0;
  let empty: string;
  if (downloads && downloads.clients.length === 0) empty = "Nothing is downloading.";
  else if (downloads && !downloads.hasReading) empty = "Downloads appear here as soon as the first reading arrives.";
  else empty = `Nothing downloading. ${imported > 0 ? `${plural(imported, "title")} imported today.` : "Nothing imported yet today."}`;

  return (
    <Panel
      title="Downloads now"
      count={rows.length > 0 ? <CountUp value={rows.length} format={(value) => plural(Math.round(value), "download")} /> : undefined}
      to="/queue"
      linkLabel="Transfers"
    >
      {rows.length === 0 ? (
        <PanelNote>{empty}</PanelNote>
      ) : (
        <FitList
          moreTo="/queue"
          moreLabel={(hidden) => `and ${hidden.toLocaleString("en-AU")} more on Transfers`}
          wrap={(shown) => (
            <ListTable
              chevron={false}
              listId="dashboard-downloads"
              columns={[
                { label: "Title", width: "minmax(0,1.35fr)", mobile: true },
                { label: "Client", width: "minmax(0,1.5fr)" },
                { label: "Progress", width: "6.5rem" },
                { label: "Speed", width: "4.5rem", align: "end" },
                { label: "Fix", width: "7rem", srOnly: true, mobile: true }
              ]}
            >
              {shown}
            </ListTable>
          )}
          rows={rows.map((item) => (
            <ListRow key={`${item.clientId}:${item.id}`} className="min-h-[2.5rem] py-1">
              <ListNameCell name={item.title} sub={item.status === "stalled" ? item.errorMessage ?? "No one is sharing it, or its data is missing" : item.releaseName} />
              <ListCell primary={<span title={item.clientName}>{item.clientName}</span>} />
              <ListCell>
                <span className="flex items-center gap-2">
                  <span aria-hidden className="block h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-surface-3">
                    <span className={cn("block h-full rounded-full transition-[width] duration-700 ease-out", item.status === "stalled" ? "bg-warning" : "bg-info")} style={{ width: `${percent(item.progress)}%` }} />
                  </span>
                  <span className="w-9 text-right tabular-nums">{percent(item.progress)}%</span>
                </span>
                <span className="mt-0.5 block truncate text-[length:var(--type-caption)] text-muted-foreground">
                  {formatEta(item.etaSeconds) === "–" ? "no time left given" : `${formatEta(item.etaSeconds)} left`}
                </span>
              </ListCell>
              <ListCell align="end" numeric>
                <span className="block truncate text-foreground">{item.status === "queued" ? "Queued" : <CountUp value={item.speedMbps} format={formatSpeed} />}</span>
                {item.status === "stalled" ? (
                  <span className="mt-0.5 flex justify-end"><Chip tone="warn" dot={false}>Stalled</Chip></span>
                ) : null}
              </ListCell>
              <ListCell align="end" mobile>
                {item.status === "stalled" && stalledWithButton.has(`${item.clientId}:${item.id}`) ? (
                  <ActionButton
                    label="Search again"
                    name={`Search again for ${item.title}`}
                    busyKey={`stalled:${item.clientId}:${item.id}`}
                    state={action}
                    onClick={() =>
                      void action.run(`stalled:${item.clientId}:${item.id}`, async () => {
                        const result = await submitQueueRemoval(item.clientId, item.id, "failed");
                        return result.message;
                      })
                    }
                  />
                ) : null}
              </ListCell>
            </ListRow>
          ))}
        />
      )}
    </Panel>
  );
}

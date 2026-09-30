/**
 * Dashboard, Downloads tab (#1082, option B).
 *
 * One headline strip, then the pipeline, what is downloading now, the clients,
 * the indexers, what needs a hand to import, and the processor. Every number
 * appears once: the strip states speed, stalled, sharing and refused releases,
 * and nothing below repeats them as a headline.
 *
 * Where the numbers come from:
 *  - the Dashboard summary (a prop): counts, client rows, pipeline, imports,
 *    indexers, processors, blocklist;
 *  - stored lists the other screens already cache: the download queue
 *    (telemetry), import recovery cases, failed jobs, processor hand-offs,
 *    indexers, processor connections.
 * Nothing here reaches a client, indexer or disk while the screen waits; only
 * a person pressing Test again, Resume or Retry does.
 */
import { useMemo } from "react";
import { Chip } from "../../components/ui/chip";
import { ListCell, ListNameCell, ListRow, ListTable } from "../../components/ui/list-card";
import { StatusLed } from "../../components/ui/status-led";
import type { DownloadQueueItem, DownloadTelemetryOverview, ProcessorConnectionItem } from "../../lib/api";
import type { DashboardClientRow, DashboardSummary } from "../../lib/api/types/operations";
import { useRouteSources } from "../../lib/route-cache";
import { submitQueueRemoval } from "../../lib/remove-download";
import { cn } from "../../lib/utils";
import {
  ActionButton,
  FactRow,
  Panel,
  PanelNote,
  ago,
  formatEta,
  formatSize,
  formatSpeed,
  plural,
  plainWords,
  send,
  until,
  useAction
} from "./downloads-kit";
import { FitList } from "./parts";
import { attentionSources, indexerNeedsButton, processorUnhealthy, type DashboardAttention } from "./attention";

type Action = ReturnType<typeof useAction>;

/* ------------------------------------------------------- downloads now */

const WORKING = ["downloading", "stalled", "queued", "notTransferring", "paused"];

function nowRows(telemetry: DownloadTelemetryOverview | undefined): DownloadQueueItem[] {
  const rank = (status: string) => WORKING.indexOf(status);
  return (telemetry?.clients ?? [])
    .flatMap((client) => client.queue)
    .filter((item) => WORKING.includes(item.status))
    .sort((a, b) => rank(a.status) - rank(b.status) || b.progress - a.progress);
}

/** Progress arrives as 0 to 100. */
function percent(progress: number): number {
  return Number.isFinite(progress) ? Math.min(100, Math.max(0, Math.round(progress))) : 0;
}

export function DownloadsNow({ summary, telemetry, attention, action }: { summary: DashboardSummary | undefined; telemetry: DownloadTelemetryOverview | undefined; attention: DashboardAttention; action: Action }) {
  // A stalled download that an import case is about is that case, which has the buttons.
  const stalledWithButton = new Set(attention.downloads.stalled.map((item) => `${item.clientId}:${item.id}`));
  const rows = nowRows(telemetry);
  const downloads = summary?.downloads;
  const imported = summary?.pipeline?.today.imported ?? 0;
  let empty: string;
  if (downloads && downloads.clients.length === 0) empty = "Nothing is downloading.";
  else if (downloads && !downloads.hasReading) empty = "Downloads appear here as soon as the first reading arrives.";
  else empty = `Nothing downloading. ${imported > 0 ? `${plural(imported, "title")} imported today.` : "Nothing imported yet today."}`;

  return (
    <Panel
      title="Downloads now"
      count={rows.length > 0 ? plural(rows.length, "download") : undefined}
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
                    <span className={cn("block h-full rounded-full", item.status === "stalled" ? "bg-warning" : "bg-info")} style={{ width: `${percent(item.progress)}%` }} />
                  </span>
                  <span className="w-9 text-right tabular-nums">{percent(item.progress)}%</span>
                </span>
                <span className="mt-0.5 block truncate text-[length:var(--type-caption)] text-muted-foreground">
                  {formatEta(item.etaSeconds) === "–" ? "no time left given" : `${formatEta(item.etaSeconds)} left`}
                </span>
              </ListCell>
              <ListCell align="end" numeric>
                <span className="block truncate text-foreground">{item.status === "queued" ? "Queued" : formatSpeed(item.speedMbps)}</span>
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

/* ------------------------------------------------------------- clients */

function clientState(client: DashboardClientRow): { tone: "ok" | "warn" | "bad" | "idle"; text: string } {
  if (!client.reachable) return { tone: "bad", text: "Not answering" };
  if (client.paused === true) return { tone: "warn", text: "Paused" };
  return { tone: "ok", text: "Answering" };
}

export function Clients({ summary, action }: { summary: DashboardSummary | undefined; action: Action }) {
  const clients = summary?.downloads?.clients ?? [];
  const online = clients.filter((client) => client.reachable).length;
  return (
    <Panel
      title="Download clients"
      count={clients.length > 0 ? `${online} of ${clients.length} answering` : undefined}
      to="/indexers/download-clients"
      linkLabel="Clients"
    >
      {clients.length === 0 ? (
        <PanelNote>No download client is set up. Add one and Deluno hands releases to it.</PanelNote>
      ) : (
        <FitList moreTo="/indexers/download-clients" moreLabel={(hidden) => `and ${hidden} more clients`} rows={
          clients.map((client) => {
            const state = clientState(client);
            return (
              <li key={client.id} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0">
                <StatusLed tone={state.tone} />
                <div className="min-w-0 flex-1 leading-tight">
                  <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">
                    {client.name}
                    {client.version ? <span className="ml-1.5 font-normal text-muted-foreground">{client.version}</span> : null}
                  </span>
                  <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">
                    {state.text}
                    {client.reachable ? `, ${plural(client.active + client.queued, "item")}` : ""}
                    {client.freeSpaceBytes ? `, ${formatSize(client.freeSpaceBytes)} free` : ""}
                  </span>
                </div>
                {!client.reachable ? (
                  <ActionButton
                    label="Test again"
                    name={`Test ${client.name} again`}
                    busyKey={`client-test:${client.id}`}
                    state={action}
                    onClick={() =>
                      void action.run(`client-test:${client.id}`, async () => {
                        const result = await send(`/api/download-clients/${encodeURIComponent(client.id)}/test`, `Deluno could not test ${client.name}.`);
                        return result?.message ?? `Tested ${client.name}.`;
                      })
                    }
                  />
                ) : null}
                {client.reachable && client.paused === true ? (
                  <ActionButton
                    label="Resume"
                    name={`Resume ${client.name}`}
                    busyKey={`client-resume:${client.id}`}
                    state={action}
                    onClick={() =>
                      void action.run(`client-resume:${client.id}`, async () => {
                        const result = await send(`/api/download-clients/${encodeURIComponent(client.id)}/resume`, `${client.name} could not be resumed.`);
                        return result?.message ?? `${client.name} is downloading again`;
                      })
                    }
                  />
                ) : null}
                {client.reachable && client.warnings > 0 ? (
                  <ActionButton
                    label={`Clear ${plural(client.warnings, "warning")}`}
                    name={`Clear ${client.name}’s warnings`}
                    busyKey={`client-warnings:${client.id}`}
                    state={action}
                    onClick={() =>
                      void action.run(`client-warnings:${client.id}`, async () => {
                        await send(`/api/download-clients/${encodeURIComponent(client.id)}/warnings/clear`, `Could not clear ${client.name}’s warnings.`);
                        return `Cleared ${client.name}’s warnings`;
                      })
                    }
                  />
                ) : null}
              </li>
            );
          })} />
      )}
    </Panel>
  );
}

/* ------------------------------------------------------------ indexers */

export function Indexers({
  summary,
  indexers,
  action,
  className
}: {
  summary: DashboardSummary | undefined;
  indexers: ReturnType<typeof useRouteSources<typeof attentionSources>>["data"]["indexers"];
  action: Action;
  className?: string;
}) {
  const section = summary?.indexers;
  const rows = useMemo(() => {
    const rank = (item: { isEnabled: boolean; healthStatus: string }) => (!item.isEnabled ? 3 : /unreach|unhealth|fail|error/i.test(item.healthStatus) ? 0 : 1);
    return [...(indexers ?? [])].sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name));
  }, [indexers]);
  const empty = (section?.total ?? rows.length) === 0;

  return (
    <Panel
      title="Indexers"
      count={section && section.total > 0 ? `${section.healthy} of ${section.total} answering` : undefined}
      to="/indexers/indexers"
      linkLabel="Indexers"
      className={className}
      footer={
        section && section.total > 0
          ? `${plural(section.queriesToday, "search")} today, ${plural(section.grabsToday, "grab")}${section.failedQueriesToday > 0 ? `, ${section.failedQueriesToday} failed` : ""}${section.rateLimited > 0 ? `. ${plural(section.rateLimited, "site")} slowed to keep within limits` : ""}`
          : undefined
      }
    >
      {empty ? (
        <PanelNote>No indexer is set up, so Deluno has nowhere to look for releases.</PanelNote>
      ) : (
        <FitList moreTo="/indexers/indexers" moreLabel={(hidden) => `and ${hidden} more indexers`} rows={
          rows.map((item) => {
            const failing = item.isEnabled && /unreach|unhealth|fail|error/i.test(item.healthStatus);
            const needsTest = indexerNeedsButton(item);
            const limited = item.isEnabled && !failing && Boolean(item.rateLimitedUntilUtc) && Date.parse(item.rateLimitedUntilUtc ?? "") > Date.now();
            const tone = !item.isEnabled ? "idle" : failing ? "bad" : limited ? "warn" : "ok";
            return (
              <li key={item.id} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-1.5 last:border-b-0">
                <StatusLed tone={tone} />
                <span className="min-w-0 flex-1 truncate text-[length:var(--type-body-sm)] text-foreground">{item.name}</span>
                <span className="whitespace-nowrap text-[length:var(--type-caption)] text-muted-foreground">
                  {!item.isEnabled ? "off" : failing ? "no answer" : limited ? `resting, back ${until(item.rateLimitedUntilUtc)}` : item.lastHealthLatencyMs ? `${Math.round(item.lastHealthLatencyMs)} ms` : "not tested yet"}
                </span>
                {needsTest ? (
                  <ActionButton
                    label="Test again"
                    name={`Test ${item.name} again`}
                    busyKey={`indexer-test:${item.id}`}
                    state={action}
                    onClick={() =>
                      void action.run(`indexer-test:${item.id}`, async () => {
                        const result = await send(`/api/indexers/${encodeURIComponent(item.id)}/test`, `Deluno could not test ${item.name}.`);
                        return result?.message ?? `Tested ${item.name}.`;
                      })
                    }
                  />
                ) : null}
              </li>
            );
          })} />
      )}
    </Panel>
  );
}
type Sources = ReturnType<typeof useRouteSources<typeof attentionSources>>["data"];

/* ----------------------------------------------------------- processor */

export function ProcessorPanel({
  summary,
  processors,
  handoffs,
  action
}: {
  summary: DashboardSummary | undefined;
  processors: ProcessorConnectionItem[];
  handoffs: Sources["handoffs"];
  action: Action;
}) {
  const section = summary?.processors;
  const title = processors.length === 1 ? processors[0].name : "Processor";
  const latest = (handoffs ?? [])[0];
  return (
    <Panel title={title} to="/settings/processing" linkLabel="Processing">
      {processors.length === 0 ? (
        <PanelNote>No processor is set up, so finished downloads are imported straight from the client. Add one under Processing if you want them cleaned first.</PanelNote>
      ) : (
        <div>
          {processors.map((processor) => {
            const unhealthy = processorUnhealthy(processor);
            return (
              <div key={processor.id} className="flex items-center gap-2 px-[var(--card-pad-x)] py-2">
                <StatusLed tone={unhealthy ? "bad" : processor.healthStatus === "healthy" ? "ok" : "idle"} />
                <div className="min-w-0 flex-1 leading-tight">
                  {processors.length > 1 ? <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{processor.name}</span> : null}
                  <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">
                    {processor.healthStatus === "healthy" ? "Ready" : processor.healthStatus === "unknown" ? "Not tested yet" : plainWords(processor.lastHealthMessage ?? processor.healthStatus, "Not answering")}
                    {processor.lastHealthTestUtc ? `, checked ${ago(processor.lastHealthTestUtc)}` : ""}
                  </span>
                </div>
                {unhealthy ? (
                  <ActionButton
                    label="Test again"
                    name={`Test ${processor.name} again`}
                    busyKey={`processor-test:${processor.id}`}
                    state={action}
                    onClick={() =>
                      void action.run(`processor-test:${processor.id}`, async () => {
                        const result = await send(`/api/integrations/processors/connections/${encodeURIComponent(processor.id)}/test`, `Deluno could not test ${processor.name}.`);
                        return result?.message ?? `Tested ${processor.name}.`;
                      })
                    }
                  />
                ) : null}
              </div>
            );
          })}
          <div className="border-t border-hairline py-1">
            <FactRow label="Handed over today">{(section?.handoffsToday ?? 0).toLocaleString("en-AU")}</FactRow>
            <FactRow label="Last one back">{latest ? `${latest.releaseName}, ${ago(latest.updatedUtc)}` : "Nothing handed over yet"}</FactRow>
          </div>
        </div>
      )}
    </Panel>
  );
}

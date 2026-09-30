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
import { Link } from "react-router-dom";
import { Chip } from "../../components/ui/chip";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable } from "../../components/ui/list-card";
import { StatusLed } from "../../components/ui/status-led";
import { SummaryStrip, type SummaryCell } from "../../components/ui/summary-strip";
import type { DownloadQueueItem, DownloadTelemetryOverview, ProcessorConnectionItem } from "../../lib/api";
import type { DashboardClientRow, DashboardSummary } from "../../lib/api/types/operations";
import { dismissFailedJob } from "../../lib/failed-jobs";
import { canTryAgain, isVanished, type RecoveryCaseRow } from "../../lib/needs-action";
import { useRouteSources } from "../../lib/route-cache";
import { submitQueueRemoval } from "../../lib/remove-download";
import { cn } from "../../lib/utils";
import {
  ActionButton,
  Panel,
  PanelLink,
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
import { attentionSources, indexerNeedsButton, processorUnhealthy, type DashboardAttention } from "./attention";
import { useDashboardAttention } from "./use-dashboard-attention";

const sources = attentionSources;

export function DownloadsTab({ summary }: { summary: DashboardSummary | undefined }) {
  const { data } = useRouteSources(sources);
  const action = useAction();
  const attention = useDashboardAttention(summary);
  const processors = useMemo(() => (data.processors ?? []).filter((processor) => processor.isEnabled), [data.processors]);
  const processorName = processors.length === 1 ? processors[0].name : processors.length > 1 ? "the processors" : "the processor";

  return (
    <div className="flex h-full min-h-0 flex-col gap-3">
      <Strip summary={summary} />
      <Pipeline summary={summary} processorName={processorName} hasProcessor={processors.length > 0} />
      <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)] lg:grid-rows-[minmax(0,3fr)_minmax(0,2fr)]">
        <DownloadsNow summary={summary} telemetry={data.telemetry} attention={attention} action={action} />
        <Clients summary={summary} action={action} />
        <Indexers summary={summary} indexers={data.indexers} action={action} className="lg:row-span-2" />
        <ImportsAndRecovery summary={summary} attention={attention} action={action} />
        <ProcessorPanel summary={summary} processors={processors} handoffs={data.handoffs} action={action} />
      </div>
    </div>
  );
}

type Action = ReturnType<typeof useAction>;

/* --------------------------------------------------------------- strip */

function Strip({ summary }: { summary: DashboardSummary | undefined }) {
  const downloads = summary?.downloads;
  const stalled = downloads?.stalled ?? 0;
  const holds = downloads?.sharingHolds ?? 0;
  const refused = summary?.blocklist?.count ?? 0;
  const cells: SummaryCell[] = [
    {
      label: "Speed now",
      value: formatSpeed(downloads?.downloadMbps),
      help: downloads?.hasReading === false ? "Waiting for the first reading from your download clients" : `${formatSpeed(downloads?.uploadMbps)} going up`,
      href: "/queue"
    },
    {
      label: "Stalled",
      value: stalled.toLocaleString("en-AU"),
      help: stalled > 0 ? "they won’t restart on their own" : "nothing is stuck",
      tone: stalled > 0 ? "warning" : undefined,
      href: "/queue"
    },
    {
      label: "Sharing",
      value: holds.toLocaleString("en-AU"),
      help: holds > 0 ? `kept for the sites they came from, ${formatSize(downloads?.sharingExtraBytes)} extra` : "nothing is being kept for a site’s sharing rules",
      href: "/queue"
    },
    {
      label: "Refused releases",
      value: refused.toLocaleString("en-AU"),
      help: refused > 0 ? "won’t be grabbed again" : "no release has been refused yet",
      href: "/search-cycles/blocklist"
    }
  ];
  return <SummaryStrip cells={cells} />;
}

/* ------------------------------------------------------------ pipeline */

function Pipeline({ summary, processorName, hasProcessor }: { summary: DashboardSummary | undefined; processorName: string; hasProcessor: boolean }) {
  const downloads = summary?.downloads;
  const reading = downloads?.hasReading !== false;
  const clientCount = downloads?.clients.length ?? 0;
  const stages = [
    {
      label: "Queued",
      count: downloads?.queued ?? 0,
      sub: reading ? "held back by the client" : "not read yet"
    },
    {
      label: "Downloading",
      count: downloads?.active ?? 0,
      sub: reading ? (clientCount > 0 ? `across ${plural(clientCount, "client")}` : "no client set up") : "not read yet"
    },
    {
      label: "Processing",
      count: downloads?.processing ?? 0,
      sub: !hasProcessor
        ? "no processor set up"
        : (downloads?.waitingForProcessor ?? 0) > 0
          ? `${downloads?.waitingForProcessor} waiting for ${processorName}`
          : `in ${processorName}`
    },
    { label: "Ready to import", count: downloads?.importReady ?? 0, sub: "waiting for Deluno to file it" },
    { label: "Imported today", count: summary?.pipeline?.today.imported ?? 0, sub: `${plural(summary?.pipeline?.today.grabbed ?? 0, "grab")} today` }
  ];
  return (
    <ListCard title="Pipeline" actions={<PanelLink to="/queue">Transfers</PanelLink>}>
      <ol className="grid grid-cols-2 gap-2 px-[var(--card-pad-x)] py-3 md:grid-cols-5">
        {stages.map((stage) => (
          <li key={stage.label} className="min-w-0 text-center">
            <Link to="/queue" className="flex flex-col items-center gap-0.5 rounded-lg py-1 hover:bg-surface-2/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
              <span
                className={cn(
                  "flex h-9 w-9 items-center justify-center rounded-full border-2 text-[length:var(--type-body-sm)] font-semibold tabular-nums",
                  stage.count > 0 ? "border-info text-info" : "border-hairline text-muted-foreground"
                )}
              >
                {stage.count.toLocaleString("en-AU")}
              </span>
              <span className="text-[length:var(--type-body-sm)] font-semibold text-foreground">{stage.label}</span>
              <span className="max-w-full truncate text-[length:var(--type-caption)] text-muted-foreground">{stage.sub}</span>
            </Link>
          </li>
        ))}
      </ol>
    </ListCard>
  );
}

/* ------------------------------------------------------- downloads now */

const WORKING = ["downloading", "stalled", "queued", "notTransferring", "paused"];

function nowRows(telemetry: DownloadTelemetryOverview | undefined): DownloadQueueItem[] {
  const rank = (status: string) => WORKING.indexOf(status);
  return (telemetry?.clients ?? [])
    .flatMap((client) => client.queue)
    .filter((item) => WORKING.includes(item.status))
    .sort((a, b) => rank(a.status) - rank(b.status) || b.progress - a.progress);
}

const ROW_LIMIT = 14;

/** Progress arrives as 0 to 100. */
function percent(progress: number): number {
  return Number.isFinite(progress) ? Math.min(100, Math.max(0, Math.round(progress))) : 0;
}

function DownloadsNow({ summary, telemetry, attention, action }: { summary: DashboardSummary | undefined; telemetry: DownloadTelemetryOverview | undefined; attention: DashboardAttention; action: Action }) {
  // A stalled download that an import case is about is that case, which has the buttons.
  const stalledWithButton = new Set(attention.downloads.stalled.map((item) => `${item.clientId}:${item.id}`));
  const rows = nowRows(telemetry);
  const shown = rows.slice(0, ROW_LIMIT);
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
      footer={rows.length > shown.length ? <Link className="text-primary hover:underline" to="/queue">{`and ${rows.length - shown.length} more on Transfers`}</Link> : undefined}
    >
      {shown.length === 0 ? (
        <PanelNote>{empty}</PanelNote>
      ) : (
        <ListTable
          chevron={false}
          columns={[
            { label: "Title", width: "minmax(0,1.8fr)", mobile: true },
            { label: "Client", width: "minmax(0,0.8fr)" },
            { label: "Progress", width: "minmax(0,1fr)" },
            { label: "Speed", width: "5.5rem", align: "end" },
            { label: "Left", width: "5.5rem", align: "end" },
            { label: "Fix", width: "7.5rem", srOnly: true, mobile: true }
          ]}
        >
          {shown.map((item) => (
            <ListRow key={`${item.clientId}:${item.id}`} className="min-h-[2.5rem] py-1">
              <ListNameCell name={item.title} sub={item.status === "stalled" ? item.errorMessage ?? "No one is sharing it, or its data is missing" : item.releaseName} />
              <ListCell primary={item.clientName} />
              <ListCell>
                <span className="flex items-center gap-2">
                  <span aria-hidden className="block h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-surface-3">
                    <span className={cn("block h-full rounded-full", item.status === "stalled" ? "bg-warning" : "bg-info")} style={{ width: `${percent(item.progress)}%` }} />
                  </span>
                  <span className="w-9 text-right tabular-nums">{percent(item.progress)}%</span>
                </span>
              </ListCell>
              <ListCell align="end" numeric primary={item.status === "queued" ? "Queued" : formatSpeed(item.speedMbps)} />
              <ListCell align="end" numeric primary={formatEta(item.etaSeconds)} />
              <ListCell align="end" mobile>
                {item.status === "stalled" ? (
                  <span className="flex items-center justify-end gap-1.5">
                    <Chip tone="warn" dot={false}>Stalled</Chip>
                    {stalledWithButton.has(`${item.clientId}:${item.id}`) ? <ActionButton
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
                    /> : null}
                  </span>
                ) : null}
              </ListCell>
            </ListRow>
          ))}
        </ListTable>
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

function Clients({ summary, action }: { summary: DashboardSummary | undefined; action: Action }) {
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
        <ul>
          {clients.map((client) => {
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
          })}
        </ul>
      )}
    </Panel>
  );
}

/* ------------------------------------------------------------ indexers */

function Indexers({
  summary,
  indexers,
  action,
  className
}: {
  summary: DashboardSummary | undefined;
  indexers: ReturnType<typeof useRouteSources<typeof sources>>["data"]["indexers"];
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
        <ul>
          {rows.map((item) => {
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
          })}
        </ul>
      )}
    </Panel>
  );
}

/* ------------------------------------------------- imports & recovery */

type Sources = ReturnType<typeof useRouteSources<typeof sources>>["data"];

function recoveryLabel(item: RecoveryCaseRow): string {
  return item.mediaType === "movie" ? "Movies" : "TV";
}

function ImportsAndRecovery({ summary, attention: all, action }: { summary: DashboardSummary | undefined; attention: DashboardAttention; action: Action }) {
  const attention = { recoveryCases: all.downloads.recoveryCases, failedImportJobs: all.downloads.failedImportJobs, failedHandoffs: all.downloads.handoffs };
  const open = attention.recoveryCases.length + attention.failedImportJobs.length + attention.failedHandoffs.length;
  const leftovers = summary?.downloads?.leftovers;

  return (
    <Panel
      title="Imports and recovery"
      count={open > 0 ? plural(open, "open case") : undefined}
      to="/queue"
      linkLabel="Transfers"
      footer={
        leftovers?.scanned && leftovers.count > 0
          ? `${plural(leftovers.count, "leftover folder")} (${formatSize(leftovers.bytes)}) left in download folders. Cases close themselves after 30 days.`
          : "Cases close themselves after 30 days."
      }
    >
      {open === 0 ? (
        <PanelNote>Nothing needs a hand. Every finished download has been filed or is on its way.</PanelNote>
      ) : (
        <ul>
          {attention.recoveryCases.map((item) => (
            <li key={`${item.mediaType}:${item.id}`} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0">
              <StatusLed tone="warn" />
              <div className="min-w-0 flex-1 leading-tight">
                <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{item.title}</span>
                <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{recoveryLabel(item)}: {item.summary}</span>
              </div>
              <RecoveryButtons item={item} action={action} />
            </li>
          ))}
          {attention.failedImportJobs.map((job, index) => (
            <li key={job.id} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0">
              <StatusLed tone="bad" />
              <div className="min-w-0 flex-1 leading-tight">
                <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">An import didn{"’"}t finish</span>
                <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{job.lastError ?? "It stopped without saying why."}</span>
              </div>
              <ActionButton
                label="Retry"
                name={`Retry failed import ${index + 1}`}
                busyKey={`job-retry:${job.id}`}
                state={action}
                onClick={() =>
                  void action.run(`job-retry:${job.id}`, async () => {
                    await send("/api/jobs/retry-failed", "The failed imports could not be tried again.");
                    return "Failed imports requeued";
                  })
                }
              />
              <ActionButton
                label="Give up"
                name={`Give up on failed import ${index + 1}`}
                busyKey={`job-dismiss:${job.id}`}
                state={action}
                variant="ghost"
                onClick={() => void action.run(`job-dismiss:${job.id}`, async () => { await dismissFailedJob(job.id); return "Import dismissed"; })}
              />
            </li>
          ))}
          {attention.failedHandoffs.map((handoff) => (
            <li key={handoff.id} className="flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0">
              <StatusLed tone="bad" />
              <div className="min-w-0 flex-1 leading-tight">
                <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{handoff.releaseName}</span>
                <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{handoff.failureMessage ?? `${handoff.processorName ?? "The processor"} could not finish it.`}</span>
              </div>
              <ActionButton
                label="Retry"
                name={`Retry ${handoff.releaseName}`}
                busyKey={`handoff-retry:${handoff.id}`}
                state={action}
                onClick={() => void action.run(`handoff-retry:${handoff.id}`, async () => (await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}/retry`, "That could not be tried again."))?.message ?? "Sent to the processor again")}
              />
              <ActionButton
                label="Give up"
                name={`Give up on ${handoff.releaseName}`}
                busyKey={`handoff-dismiss:${handoff.id}`}
                state={action}
                variant="ghost"
                onClick={() => void action.run(`handoff-dismiss:${handoff.id}`, async () => { await send(`/api/integrations/processors/handoffs/${encodeURIComponent(handoff.id)}/dismiss`, "That could not be dismissed."); return "Dismissed"; })}
              />
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

function RecoveryButtons({ item, action }: { item: RecoveryCaseRow; action: Action }) {
  const collection = item.mediaType === "movie" ? "movies" : "series";
  const base = `/api/${collection}/import-recovery/${encodeURIComponent(item.id)}`;
  return (
    <span className="flex shrink-0 items-center gap-1.5">
      {canTryAgain(item) ? (
        <ActionButton
          label={isVanished(item) ? "Search again" : "Retry"}
          name={`${isVanished(item) ? "Search again for" : "Retry"} ${item.title}`}
          busyKey={`recovery-retry:${item.id}`}
          state={action}
          onClick={() => void action.run(`recovery-retry:${item.id}`, async () => (await send(`${base}/retry`, "That could not be tried again."))?.message ?? `${item.title} is being tried again.`)}
        />
      ) : (
        <Link
          to="/queue"
          aria-label={`Review ${item.title}`}
          className="inline-flex h-[var(--control-height-sm)] items-center rounded-[10px] border border-hairline px-3 text-[length:var(--button-font-size-sm)] font-medium text-foreground hover:bg-secondary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          Review
        </Link>
      )}
      <ActionButton
        label="Give up"
        name={`Give up on ${item.title}`}
        busyKey={`recovery-dismiss:${item.id}`}
        state={action}
        variant="ghost"
        onClick={() => void action.run(`recovery-dismiss:${item.id}`, async () => { await send(`${base}/dismiss`, "That case could not be dismissed."); return `${item.title} dismissed`; })}
      />
    </span>
  );
}

/* ----------------------------------------------------------- processor */

function ProcessorPanel({
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
            <div className="flex items-baseline justify-between gap-3 px-[var(--card-pad-x)] py-1 text-[length:var(--type-body-sm)]">
              <span className="text-muted-foreground">Handed over today</span>
              <span className="font-medium tabular-nums text-foreground">{(section?.handoffsToday ?? 0).toLocaleString("en-AU")}</span>
            </div>
            <div className="flex items-baseline justify-between gap-3 px-[var(--card-pad-x)] py-1 text-[length:var(--type-body-sm)]">
              <span className="text-muted-foreground">Last one back</span>
              <span className="min-w-0 truncate text-right font-medium text-foreground">
                {latest ? `${latest.releaseName}, ${ago(latest.updatedUtc)}` : "Nothing handed over yet"}
              </span>
            </div>
          </div>
        </div>
      )}
    </Panel>
  );
}

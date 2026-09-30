/**
 * Connections (#1087): the download clients, the indexers and the processor in
 * one panel, each row with the button that clears its problem (Test again,
 * Resume, Clear warnings). One panel rather than three, so the System tab has
 * room for the health checks and the machine.
 *
 * Where the numbers come from: the Dashboard summary (client rows, indexer and
 * processor counts) and two stored lists the other screens already cache (the
 * indexers and the processor connections). Nothing here reaches a client,
 * indexer or processor while the screen waits; only a person pressing a button
 * does.
 */
import { StatusLed } from "../../components/ui/status-led";
import type { IndexerItem, ProcessorConnectionItem } from "../../lib/api";
import type { DashboardClientRow, DashboardSummary } from "../../lib/api/types/operations";
import { indexerNeedsButton, processorUnhealthy } from "./attention";
import { ActionButton, Panel, ago, formatSize, plainWords, plural, send, until, type ActionState } from "./downloads-kit";
import { FitList, Group } from "./parts";

const row = "flex items-center gap-2 border-b border-hairline px-[var(--card-pad-x)] py-1.5 last:border-b-0";
const failingText = /unreach|unhealth|fail|error/i;

function clientState(client: DashboardClientRow): { tone: "ok" | "warn" | "bad" | "idle"; text: string } {
  if (!client.reachable) return { tone: "bad", text: "Not answering" };
  if (client.paused === true) return { tone: "warn", text: "Paused" };
  return { tone: "ok", text: "Answering" };
}

function clientRows(clients: DashboardClientRow[], action: ActionState) {
  return clients.map((client) => {
    const state = clientState(client);
    return (
      <li key={`client:${client.id}`} className={row}>
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
  });
}

function indexerRows(indexers: IndexerItem[], action: ActionState) {
  const rank = (item: { isEnabled: boolean; healthStatus: string }) => (!item.isEnabled ? 3 : failingText.test(item.healthStatus) ? 0 : 1);
  return [...indexers]
    .sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name))
    .map((item) => {
      const failing = item.isEnabled && failingText.test(item.healthStatus);
      const limited = item.isEnabled && !failing && Boolean(item.rateLimitedUntilUtc) && Date.parse(item.rateLimitedUntilUtc ?? "") > Date.now();
      const tone = !item.isEnabled ? "idle" : failing ? "bad" : limited ? "warn" : "ok";
      return (
        <li key={`indexer:${item.id}`} className={row}>
          <StatusLed tone={tone} />
          <span className="min-w-0 flex-1 truncate text-[length:var(--type-body-sm)] text-foreground">{item.name}</span>
          <span className="whitespace-nowrap text-[length:var(--type-caption)] text-muted-foreground">
            {!item.isEnabled ? "off" : failing ? "no answer" : limited ? `resting, back ${until(item.rateLimitedUntilUtc)}` : item.lastHealthLatencyMs ? `${Math.round(item.lastHealthLatencyMs)} ms` : "not tested yet"}
          </span>
          {indexerNeedsButton(item) ? (
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
    });
}

function processorRows(processors: ProcessorConnectionItem[], action: ActionState) {
  return processors.map((processor) => {
    const unhealthy = processorUnhealthy(processor);
    return (
      <li key={`processor:${processor.id}`} className={row}>
        <StatusLed tone={unhealthy ? "bad" : processor.healthStatus === "healthy" ? "ok" : "idle"} />
        <div className="min-w-0 flex-1 leading-tight">
          <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{processor.name}</span>
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
      </li>
    );
  });
}

const none = (key: string, text: string) => (
  <li key={key} className="px-[var(--card-pad-x)] py-2 text-[length:var(--type-caption)] text-muted-foreground">{text}</li>
);

export function Connections({
  summary,
  indexers,
  processors,
  action,
  className
}: {
  summary: DashboardSummary | undefined;
  indexers: IndexerItem[] | undefined;
  processors: ProcessorConnectionItem[];
  action: ActionState;
  className?: string;
}) {
  const clients = summary?.downloads?.clients ?? [];
  const indexerList = indexers ?? [];
  const indexerSection = summary?.indexers;
  const answering = clients.filter((client) => client.reachable).length + (indexerSection?.healthy ?? indexerList.filter((item) => item.isEnabled && !failingText.test(item.healthStatus)).length) + processors.filter((item) => !processorUnhealthy(item)).length;
  const total = clients.length + (indexerSection?.total ?? indexerList.length) + processors.length;
  const handedOver = summary?.processors?.handoffsToday ?? 0;

  // A caption is never the last thing shown: a heading with nothing under it is a cut-off row.
  const rows = [
    <Group key="h:clients">Download clients</Group>,
    ...(clients.length === 0 ? [none("none:clients", "No download client is set up. Add one and Deluno hands releases to it.")] : clientRows(clients, action)),
    <Group key="h:indexers">Indexers</Group>,
    ...(indexerList.length === 0 && (indexerSection?.total ?? 0) === 0 ? [none("none:indexers", "No indexer is set up, so Deluno has nowhere to look for releases.")] : indexerRows(indexerList, action)),
    <Group key="h:processor">Processor</Group>,
    ...(processors.length === 0 ? [none("none:processors", "No processor is set up, so finished downloads are imported straight from the client.")] : processorRows(processors, action))
  ];
  const headings = new Set(rows.map((node, index) => (String(node.key).startsWith("h:") ? index : -1)).filter((index) => index >= 0));

  return (
    <Panel
      title="Connections"
      count={total > 0 ? `${answering} of ${total} answering` : undefined}
      to="/indexers/download-clients"
      linkLabel="Open"
      className={className}
      footer={
        indexerSection && indexerSection.total > 0
          ? `${plural(indexerSection.queriesToday, "search", "searches")} today, ${plural(indexerSection.grabsToday, "grab")}${indexerSection.failedQueriesToday > 0 ? `, ${indexerSection.failedQueriesToday} failed` : ""}${processors.length > 0 ? `. ${plural(handedOver, "download")} handed to the processor today` : ""}`
          : undefined
      }
    >
      <FitList wrap={(shown) => <div>{shown}</div>} moreTo="/indexers/indexers" moreLabel={(hidden) => `and ${hidden.toLocaleString("en-AU")} more connections`} isHeading={(index) => headings.has(index)} rows={rows} />
    </Panel>
  );
}

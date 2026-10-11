import { toneSortValue } from "../../lib/status-tones";
import type { IndexerScoreboardRow, IndexerScoreboardSnapshot } from "../../lib/api";
import { Chip, type ChipProps } from "../../components/ui/chip";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../../components/ui/list-card";
import { SummaryStrip } from "../../components/ui/summary-strip";

const numberFormat = new Intl.NumberFormat();
const percentFormat = new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 1 });

/** A removed indexer's history can carry no name at all: say what it is rather than print a row with no label. */
function scoreboardName(row: Pick<IndexerScoreboardRow, "name" | "healthStatus">) {
  const name = row.name?.trim();
  if (name) return name;
  return row.healthStatus === "removed" ? "A removed indexer" : "An unnamed indexer";
}

export function IndexerScoreboard({ snapshot }: { snapshot: IndexerScoreboardSnapshot }) {
  const rows = snapshot.indexers;
  // Each metric's bars are scaled to the biggest reading in that column, so rows compare down a column.
  const max = {
    queries: Math.max(0, ...rows.map((row) => row.totalQueries)),
    grabs: Math.max(0, ...rows.map((row) => row.successfulGrabs)),
    conversion: Math.max(0, ...rows.map((row) => row.queryToGrabConversion ?? 0)),
    response: Math.max(0, ...rows.map((row) => row.averageResponseMilliseconds ?? 0)),
    failureRate: Math.max(0, ...rows.map((row) => row.failureRate))
  };
  return (
    <div className="grid gap-(--page-gap)">
      <SummaryStrip
        cells={[
          {
            label: "Active indexers",
            value: `${snapshot.activeIndexers}/${snapshot.totalIndexers}`,
            help: snapshot.totalIndexers ? "enabled sources" : "add a source to start measuring"
          },
          {
            label: "Total queries",
            value: numberFormat.format(snapshot.totalQueries),
            help: `last ${snapshot.windowDays} days`
          },
          {
            label: "Total grabs",
            value: numberFormat.format(snapshot.totalGrabs),
            help: `${numberFormat.format(snapshot.successfulGrabs)} successful`,
            tone: snapshot.successfulGrabs ? "success" : undefined
          },
          {
            label: "Query → grab",
            value: snapshot.conversionRate === null ? "—" : percentFormat.format(snapshot.conversionRate),
            help: "successful grabs per query",
            tone: snapshot.conversionRate && snapshot.conversionRate > 0 ? "success" : undefined
          }
        ]}
      />

      <section className="rounded-2xl border border-primary/20 bg-primary/6 px-(--card-pad-x) py-(--card-pad-y) shadow-card">
        <p className="text-(length:--type-micro) font-semibold uppercase tracking-widest text-primary">Deluno's read</p>
        <p className="mt-1 text-(length:--type-body-sm) text-foreground">{snapshot.insight}</p>
        <p className="mt-1 text-(length:--type-caption) text-muted-foreground">
          Window: {snapshot.windowDays} days. Query history is retained for 30 days and excludes credentials and full request URLs.
        </p>
      </section>

      {rows.length === 0 ? (
        <ListCard title="Nothing recorded yet" titleCase={false}>
          <ListEmpty
            title="No indexer activity yet"
            description="Run a search or test an indexer. Deluno will record the response, result and latency here so source quality is visible over time."
          />
        </ListCard>
      ) : (
        <>
          <ListCard title={`Last ${snapshot.windowDays} days`} titleCase={false}>
            <ListTable
              listId="indexer-scoreboard"
              items={rows}
              chevron={false}
              columns={[
                { id: "indexer", label: "Indexer", sort: (row) => scoreboardName(row).toLowerCase() },
                { id: "queries", label: "Queries", mobile: true, sort: (row) => row.totalQueries },
                { id: "grabs", label: "Grabs", mobile: true, sort: (row) => row.successfulGrabs },
                { id: "conversion", label: "Grab rate", mobile: true, sort: (row) => row.queryToGrabConversion },
                { id: "response", label: "Response", mobile: true, sort: (row) => row.averageResponseMilliseconds },
                { id: "failureRate", label: "Failed", mobile: true, sort: (row) => row.failureRate },
                { id: "status", label: "Status", width: LIST_TRACK.status, sort: (row) => toneSortValue(statusTone(row), statusLabel(row)) }
              ]}
            >
              {(sorted) => sorted.map((row) => (
                <ListRow key={row.id}>
                  <ListNameCell name={scoreboardName(row)} sub={<>{row.recommendation}<Chip tone={statusTone(row)} className="mt-1 flex w-fit md:hidden">{statusLabel(row)}</Chip></>} />
                  <MetricCell reading={row.totalQueries} max={max.queries} text={numberFormat.format(row.totalQueries)} detail={`Search ${numberFormat.format(row.searchQueries)} · RSS ${numberFormat.format(row.rssQueries)} · Auth ${numberFormat.format(row.authQueries)}`} tone="primary" />
                  <MetricCell reading={row.successfulGrabs} max={max.grabs} text={numberFormat.format(row.successfulGrabs)} detail={`${numberFormat.format(row.totalGrabs)} grabs in all`} tone="success" />
                  <MetricCell reading={row.queryToGrabConversion} max={max.conversion} text={row.queryToGrabConversion === null ? "—" : formatPercent(row.queryToGrabConversion)} detail="successful grabs per query" tone="success" />
                  <MetricCell reading={row.averageResponseMilliseconds} max={max.response} text={row.averageResponseMilliseconds === null ? "—" : `${numberFormat.format(Math.round(row.averageResponseMilliseconds))} ms`} detail={`${numberFormat.format(row.candidatesReturned)} candidates returned`} tone="info" />
                  <MetricCell reading={row.failureRate} max={max.failureRate} text={formatPercent(row.failureRate)} detail={`${numberFormat.format(row.failedQueries)} failed`} tone="danger" warn={row.failureRate >= 0.25} />
                  <ListCell>
                    <Chip tone={statusTone(row)}>{statusLabel(row)}</Chip>
                  </ListCell>
                </ListRow>
              ))}
            </ListTable>
          </ListCard>
        </>
      )}
    </div>
  );
}

/** One reading with a thin bar under it, scaled across the table's rows. The detail is the cell's tooltip. */
function MetricCell({ reading, max, text, detail, tone, warn = false }: {
  reading: number | null;
  max: number;
  text: string;
  detail: string;
  tone: "primary" | "success" | "info" | "danger";
  warn?: boolean;
}) {
  const width = reading === null || !Number.isFinite(reading) || !Number.isFinite(max) || reading <= 0 || max <= 0 ? 0 : Math.max(2, (reading / max) * 100);
  return (
    <ListCell numeric mobile>
      <span title={detail} className="block min-w-14">
        <span className={`block truncate ${warn ? "text-destructive" : "text-foreground"}`}>{text}</span>
        <span aria-hidden className="mt-1 block h-1.5 overflow-hidden rounded-full bg-surface-3">
          <span className={`block h-full rounded-full ${barClass(tone)}`} style={{ width: `${width}%` }} />
        </span>
      </span>
    </ListCell>
  );
}

function formatPercent(value: number) {
  return percentFormat.format(Math.max(0, value));
}

function barClass(tone: "primary" | "success" | "info" | "danger") {
  return tone === "success" ? "bg-success" : tone === "info" ? "bg-info" : tone === "danger" ? "bg-destructive" : "bg-primary";
}

function statusLabel(row: IndexerScoreboardRow) {
  return row.isEnabled ? row.healthStatus : row.healthStatus === "removed" ? "Removed" : "Paused";
}

function statusTone(row: IndexerScoreboardRow): ChipProps["tone"] {
  if (!row.isEnabled || row.healthStatus === "removed") return "idle";
  if (row.failureRate >= 0.25 || row.healthStatus === "unreachable" || row.healthStatus === "degraded") return "warn";
  if (row.healthStatus === "healthy" || row.healthStatus === "ok") return "ok";
  return "idle";
}

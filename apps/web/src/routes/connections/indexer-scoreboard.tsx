import type { IndexerScoreboardRow, IndexerScoreboardSnapshot } from "../../lib/api";
import { Chip, type ChipProps } from "../../components/ui/chip";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../../components/ui/list-card";
import { SummaryStrip } from "../../components/ui/summary-strip";

const numberFormat = new Intl.NumberFormat();
const percentFormat = new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 1 });

export function IndexerScoreboard({ snapshot }: { snapshot: IndexerScoreboardSnapshot }) {
  const rows = snapshot.indexers;
  return (
    <div className="grid gap-[var(--page-gap)]">
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

      <section className="rounded-2xl border border-primary/20 bg-primary/[0.06] px-[var(--card-pad-x)] py-[var(--card-pad-y)] shadow-card">
        <p className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-primary">Deluno's read</p>
        <p className="mt-1 text-[length:var(--type-body-sm)] text-foreground">{snapshot.insight}</p>
        <p className="mt-1 text-[length:var(--type-caption)] text-muted-foreground">
          Window: {snapshot.windowDays} days. Query history is retained for 30 days and excludes credentials and full request URLs.
        </p>
      </section>

      {rows.length === 0 ? (
        <ListCard title="Indexer scoreboard">
          <ListEmpty
            title="No indexer activity yet"
            description="Run a search or test an indexer. Deluno will record the response, result and latency here so source quality is visible over time."
          />
        </ListCard>
      ) : (
        <>
          <div className="grid gap-[var(--page-gap)] md:grid-cols-2">
            <ScoreboardBars title="Queries" rows={rows} value={(row) => row.totalQueries} empty="No queries in this window." />
            <ScoreboardBars title="Successful grabs" rows={rows} value={(row) => row.successfulGrabs} empty="No successful grabs in this window." tone="success" />
            <ScoreboardBars title="Average response" rows={rows} value={(row) => row.averageResponseMilliseconds ?? 0} format={(value) => `${numberFormat.format(Math.round(value))} ms`} empty="No response history in this window." tone="info" />
            <ScoreboardBars title="Failure rate" rows={rows} value={(row) => row.failureRate * 100} format={(value) => `${value.toFixed(1)}%`} empty="No failed queries in this window." tone="danger" />
          </div>

          <ListCard title="Indexer scoreboard" count={`last ${snapshot.windowDays} days`}>
            <ListTable columns={[{ label: "Indexer" }, { label: "Queries" }, { label: "Response" }, { label: "Failure rate" }, { label: "Grabs" }, { label: "Conversion" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
              {rows.map((row) => (
                <ListRow key={row.id}>
                  <ListNameCell name={row.name} sub={row.recommendation} />
                  <ListCell numeric primary={numberFormat.format(row.totalQueries)} secondary={`Search ${numberFormat.format(row.searchQueries)} · RSS ${numberFormat.format(row.rssQueries)} · Auth ${numberFormat.format(row.authQueries)}`} />
                  <ListCell numeric primary={row.averageResponseMilliseconds === null ? "—" : `${numberFormat.format(Math.round(row.averageResponseMilliseconds))} ms`} secondary={numberFormat.format(row.candidatesReturned) + " candidates"} />
                  <ListCell numeric primary={<span className={row.failureRate >= 0.25 ? "text-destructive" : "text-foreground"}>{formatPercent(row.failureRate)}</span>} secondary={`${numberFormat.format(row.failedQueries)} failed`} />
                  <ListCell numeric primary={numberFormat.format(row.totalGrabs)} secondary={`${numberFormat.format(row.successfulGrabs)} successful`} />
                  <ListCell numeric primary={row.queryToGrabConversion === null ? "—" : formatPercent(row.queryToGrabConversion)} secondary="successful grabs / queries" />
                  <ListCell mobile>
                    <Chip tone={statusTone(row)}>{row.isEnabled ? row.healthStatus : row.healthStatus === "removed" ? "Removed" : "Paused"}</Chip>
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

function ScoreboardBars({
  title,
  rows,
  value,
  format = (reading) => numberFormat.format(Math.round(reading)),
  empty,
  tone = "primary"
}: {
  title: string;
  rows: IndexerScoreboardRow[];
  value: (row: IndexerScoreboardRow) => number;
  format?: (reading: number) => string;
  empty: string;
  tone?: "primary" | "success" | "info" | "danger";
}) {
  const values = rows.map(value);
  const max = Math.max(...values, 0);
  const hasValue = values.some((reading) => reading > 0);
  return (
    <ListCard title={title}>
      {!hasValue ? (
        <ListEmpty title={empty} />
      ) : (
        <div className="space-y-3 px-[var(--card-pad-x)] py-[var(--card-pad-y)]">
          {rows.slice().sort((left, right) => value(right) - value(left)).map((row) => {
            const reading = value(row);
            return (
              <div key={row.id} className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-3">
                <div className="min-w-0">
                  <div className="flex items-center justify-between gap-3 text-[length:var(--type-caption)]">
                    <span className="truncate font-medium text-foreground">{row.name}</span>
                    <span className="shrink-0 tabular-nums text-muted-foreground">{format(reading)}</span>
                  </div>
                  <div className="mt-1 h-1.5 overflow-hidden rounded-full bg-surface-3">
                    <div className={`h-full rounded-full ${barClass(tone)}`} style={{ width: `${max === 0 ? 0 : Math.max(2, (reading / max) * 100)}%` }} />
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      )}
    </ListCard>
  );
}

function formatPercent(value: number) {
  return percentFormat.format(Math.max(0, value));
}

function barClass(tone: "primary" | "success" | "info" | "danger") {
  return tone === "success" ? "bg-success" : tone === "info" ? "bg-info" : tone === "danger" ? "bg-destructive" : "bg-primary";
}

function statusTone(row: IndexerScoreboardRow): ChipProps["tone"] {
  if (!row.isEnabled || row.healthStatus === "removed") return "idle";
  if (row.failureRate >= 0.25 || row.healthStatus === "unreachable" || row.healthStatus === "degraded") return "warn";
  if (row.healthStatus === "healthy" || row.healthStatus === "ok") return "ok";
  return "idle";
}

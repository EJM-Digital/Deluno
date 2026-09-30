/**
 * The indexers Deluno is asking (#1096): what each one did today, from the
 * stored record of every query (no indexer is asked while the screen waits):
 * how many times it was asked, how many releases were grabbed from it, how many
 * asks failed, and when it last answered. A quiet one says it was not asked.
 *
 * An indexer that is not answering is not listed here: Needs you owns it, with
 * its own Test again button, so a problem is in one place with one button.
 */
import { Link } from "react-router-dom";
import { StatusLed } from "../../../components/ui/status-led";
import type { IndexerItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import type { Tone } from "../../../lib/status-tones";
import { cn } from "../../../lib/utils";
import { indexerNeedsButton } from "../attention";
import { ago } from "../format";
import { FitList, Panel, Plain } from "../parts";

/** One indexer's row of today's scoreboard (`GET /api/indexers/scoreboard?days=1`). */
export interface IndexerToday {
  id: string;
  name: string;
  totalQueries: number;
  failedQueries: number;
  successfulGrabs: number;
  lastQueryUtc?: string | null;
}

export interface IndexerScoreboardToday {
  indexers: IndexerToday[];
}

export function indexerAnswer(indexer: IndexerItem, today: IndexerToday | undefined, now: number): { tone: Tone; text: string } {
  if (!indexer.isEnabled) return { tone: "idle", text: "switched off" };
  if (indexer.rateLimitedUntilUtc && Date.parse(indexer.rateLimitedUntilUtc) > now) return { tone: "warn", text: "asked too often, waiting" };
  if (indexer.healthStatus === "unknown" || !indexer.healthStatus) return { tone: "idle", text: "not asked yet" };
  const asked = today?.totalQueries ?? 0;
  if (asked === 0) return { tone: "ok", text: "not asked today" };
  const failed = today?.failedQueries ?? 0;
  const grabbed = today?.successfulGrabs ?? 0;
  const parts = [`asked ${asked.toLocaleString("en-AU")} ${asked === 1 ? "time" : "times"}`];
  if (grabbed > 0) parts.push(`${grabbed.toLocaleString("en-AU")} grabbed`);
  if (failed > 0) parts.push(`${failed.toLocaleString("en-AU")} failed`);
  return { tone: failed > 0 ? "warn" : "ok", text: parts.join(" · ") };
}

export function Indexers({ indexers, scoreboard, summary, now }: { indexers: IndexerItem[]; scoreboard?: IndexerScoreboardToday | null; summary: DashboardSummary | undefined; now: number }) {
  const stats = summary?.indexers;
  const rows = Array.isArray(scoreboard?.indexers) ? scoreboard.indexers : null;
  const todayById = new Map((rows ?? []).map((row) => [row.id, row]));
  const asked = rows ? rows.reduce((sum, row) => sum + (row.totalQueries ?? 0), 0) : stats?.queriesToday;
  const count = asked !== undefined ? `${asked.toLocaleString("en-AU")} asked today` : undefined;
  const sorted = indexers
    .filter((indexer) => !indexerNeedsButton(indexer))
    .sort((a, b) => Number(b.isEnabled) - Number(a.isEnabled) || (todayById.get(b.id)?.totalQueries ?? 0) - (todayById.get(a.id)?.totalQueries ?? 0) || a.priority - b.priority);
  return (
    <Panel title="Indexers" count={count ? <span className="whitespace-nowrap">{count}</span> : undefined} to="/indexers/indexers" toLabel="Manage">
      {sorted.length === 0 ? (
        <Plain>{indexers.length === 0 ? "No indexers are set up, so nothing can be searched for." : "No indexer is answering. Needs you has each one."}</Plain>
      ) : (
        <FitList
          moreTo="/indexers/indexers"
          rows={sorted.map((indexer) => {
            const today = todayById.get(indexer.id);
            const answer = indexerAnswer(indexer, today, now);
            const last = today?.lastQueryUtc ?? null;
            return (
              <li key={indexer.id} className="border-b border-hairline last:border-b-0">
                <Link to="/indexers/indexers" aria-label={`${indexer.name}: ${answer.text}`} className="flex items-center gap-2.5 px-[var(--card-pad-x)] py-1.5 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
                  <StatusLed tone={answer.tone} pulse={answer.tone === "warn"} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[length:var(--type-body-sm)] text-foreground">{indexer.name}</span>
                    <span className={cn("block truncate text-[length:var(--type-caption)]", answer.tone === "warn" ? "text-warning" : "text-muted-foreground")}>{answer.text}</span>
                  </span>
                  {last ? <span className="shrink-0 whitespace-nowrap text-[length:var(--type-caption)] tabular-nums text-muted-foreground" title="When it last answered">{ago(last, now)}</span> : null}
                </Link>
              </li>
            );
          })}
        />
      )}
    </Panel>
  );
}

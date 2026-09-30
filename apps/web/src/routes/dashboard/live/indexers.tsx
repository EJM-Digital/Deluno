/**
 * The indexers Deluno asks (#1096): each one with how it answered last, from
 * the stored health of every indexer (no indexer is asked while the screen
 * waits), and what was asked of them all today from the summary.
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

export function indexerAnswer(indexer: IndexerItem, now: number): { tone: Tone; text: string } {
  if (!indexer.isEnabled) return { tone: "idle", text: "switched off" };
  if (indexer.rateLimitedUntilUtc && Date.parse(indexer.rateLimitedUntilUtc) > now) return { tone: "warn", text: "asked too often, waiting" };
  if (indexer.healthStatus === "healthy") return { tone: "ok", text: indexer.lastHealthLatencyMs ? `answered in ${Math.round(indexer.lastHealthLatencyMs)} ms` : "answering" };
  if (indexer.healthStatus === "unknown" || !indexer.healthStatus) return { tone: "idle", text: "not asked yet" };
  return { tone: "bad", text: indexer.lastHealthMessage ? indexer.lastHealthMessage : "not answering" };
}

export function Indexers({ indexers, summary, now }: { indexers: IndexerItem[]; summary: DashboardSummary | undefined; now: number }) {
  const stats = summary?.indexers;
  const count = stats ? (stats.failedQueriesToday > 0 ? `${stats.failedQueriesToday.toLocaleString("en-AU")} failed` : `${stats.queriesToday.toLocaleString("en-AU")} asked`) : undefined;
  const sorted = indexers.filter((indexer) => !indexerNeedsButton(indexer)).sort((a, b) => Number(b.isEnabled) - Number(a.isEnabled) || a.priority - b.priority);
  return (
    <Panel title="Indexers" count={count ? <span className="whitespace-nowrap">{count}</span> : undefined} to="/indexers/indexers" toLabel="Manage">
      {sorted.length === 0 ? (
        <Plain>{indexers.length === 0 ? "No indexers are set up, so nothing can be searched for." : "No indexer is answering. Needs you has each one."}</Plain>
      ) : (
        <FitList
          moreTo="/indexers/indexers"
          rows={sorted.map((indexer) => {
            const answer = indexerAnswer(indexer, now);
            return (
              <li key={indexer.id} className="border-b border-hairline last:border-b-0">
                <Link to="/indexers/indexers" aria-label={`${indexer.name}: ${answer.text}`} className="flex items-center gap-2.5 px-[var(--card-pad-x)] py-1.5 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring">
                  <StatusLed tone={answer.tone} pulse={answer.tone === "bad" || answer.tone === "warn"} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[length:var(--type-body-sm)] text-foreground">{indexer.name}</span>
                    <span className={cn("block truncate text-[length:var(--type-caption)]", answer.tone === "bad" ? "text-destructive" : "text-muted-foreground")}>
                      {answer.text}
                      {indexer.lastHealthTestUtc && answer.tone !== "idle" ? ` · ${ago(indexer.lastHealthTestUtc)}` : ""}
                    </span>
                  </span>
                </Link>
              </li>
            );
          })}
        />
      )}
    </Panel>
  );
}

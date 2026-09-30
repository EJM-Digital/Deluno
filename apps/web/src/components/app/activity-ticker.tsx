/**
 * The live activity ticker (#270).
 *
 * Deluno already publishes every activity event to the `activity` realtime
 * group; until now only a panel inside System listened. On the dashboard it is
 * the thing that makes the pane feel alive: you can watch a search run, a grab
 * land and an import finish without touching the page.
 *
 * Seeded from `/api/activity` so it is populated on arrival rather than empty
 * until something happens, then appended to from the stream. The connection
 * state is shown honestly — a stalled socket must not look like a quiet system.
 *
 * Contract: GET /api/activity?pageSize=…, realtime `ActivityEventAdded`.
 */
import { useState } from "react";
import { Link } from "react-router-dom";
import { Button } from "../ui/button";
import { ListCard, ListEmpty } from "../ui/list-card";
import { StatusLed } from "../ui/status-led";
import { cn } from "../../lib/utils";
import type { ActivityEventItem, ActivityStatsSnapshot } from "../../lib/api";
import { useLiveInterval } from "../../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvent, useSignalRStatus } from "../../lib/use-signalr";
import { useDisplayPreferences } from "../../lib/display-preferences";
import { eventLabel, formatEventAge, SEVERITY_LED, type ActivitySeverity } from "../../lib/activity-format";

type Severity = ActivitySeverity;

interface TickerEvent {
  id: string;
  message: string;
  category: string;
  label?: string;
  severity: Severity;
  createdUtc: string;
  /** How many times in a row this exact message occurred. Absent means once. */
  repeats?: number;
}

export function ActivityTicker({ seed, limit = 10, stats }: { seed: ActivityEventItem[]; limit?: number; stats?: ActivityStatsSnapshot }) {
  const status = useSignalRStatus();
  const { preferences } = useDisplayPreferences();
  const [live, setLive] = useState<TickerEvent[]>([]);

  // "2m ago" has to keep counting. Without this the ages freeze at whatever
  // they were when the last event arrived, which on a quiet system is the
  // difference between "nothing has happened for an hour" and a lie.
  const [, setNow] = useState(() => Date.now());
  useLiveInterval(() => setNow(Date.now()), 30_000);

  useSignalREvent("ActivityEventAdded", RealtimeGroups.Activity, (event) => {
    setLive((current) => {
      // The seed and the stream overlap at the moment the page loads, so an
      // event can arrive that is already in the list.
      if (current.some((item) => item.id === event.id)) return current;
      return [
        { id: event.id, message: event.message, category: event.category, label: event.label, severity: event.severity, createdUtc: event.createdUtc },
        ...current
      ].slice(0, limit);
    });
  });

  const seeded: TickerEvent[] = seed.map((item) => ({
    id: item.id,
    message: item.message,
    category: item.category,
    label: item.label,
    severity: (item.severity ?? "info") as Severity,
    createdUtc: item.createdUtc
  }));

  const seenLive = new Set(live.map((item) => item.id));
  const events = collapseRepeats([...live, ...seeded.filter((item) => !seenLive.has(item.id))]).slice(0, limit);
  const connected = status === "connected";

  return (
    <ListCard
      title="Live activity"
      count={
        <span className="inline-flex items-center gap-1.5">
          <span
            aria-hidden
            className={cn(
              "h-1.5 w-1.5 rounded-full",
              connected ? "bg-success motion-safe:animate-pulse" : status === "reconnecting" ? "bg-warning" : "bg-muted-foreground/50"
            )}
          />
          {connected ? "streaming" : status === "reconnecting" ? "reconnecting…" : "not connected"}
        </span>
      }
      actions={
        <Button asChild type="button" variant="outline" size="sm">
          <Link to="/activity">Open Activity</Link>
        </Button>
      }
    >
      {events.length === 0 ? (
        <ListEmpty
          title="Nothing has happened yet"
          description="Searches, grabs and imports appear here the moment they happen."
        />
      ) : (
        // Deliberately not a ListTable: in a third-width panel the category
        // column had nowhere to truncate to and collided with the timestamp.
        // Severity moves to a leading light, and the time sits on its own line
        // under the message, which reads better narrow than any column split.
        <div className="max-h-[232px] overflow-y-auto">
          {events.map((event) => (
            <div
              key={event.id}
              className={cn(
                "flex min-h-[46px] items-start gap-2.5 border-b border-hairline px-[var(--card-pad-x)] py-2 last:border-b-0",
                seenLive.has(event.id) && "activity-arrival"
              )}
            >
              <StatusLed tone={SEVERITY_LED[event.severity]} size={6} className="mt-1.5" />
              <span className="min-w-0 flex-1">
                <span className="block truncate text-[length:var(--type-caption)] text-foreground">
                  {event.message}
                  {event.repeats && event.repeats > 1 ? (
                    <span className="ml-1 text-muted-foreground">×{event.repeats}</span>
                  ) : null}
                </span>
                <span className="mt-0.5 block truncate text-[length:var(--type-micro)] text-muted-foreground">
                  {eventLabel(event)} · {formatEventAge(event.createdUtc, preferences)}
                </span>
              </span>
            </div>
          ))}
        </div>
      )}
      {stats ? (
        <div
          className="flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-hairline px-[var(--card-pad-x)] py-2 text-[length:var(--type-micro)] text-muted-foreground"
          aria-label="Automation counts for the last 24 hours"
        >
          <span>Last 24h:</span>
          <span>{stats.searchesRun} {stats.searchesRun === 1 ? "search" : "searches"}</span>
          <span>{stats.downloadsStarted} {stats.downloadsStarted === 1 ? "download" : "downloads"}</span>
          <span>{stats.importsCompleted} {stats.importsCompleted === 1 ? "import" : "imports"}</span>
          <span>{stats.cleanupRemovals} cleaned up</span>
          <span>{stats.retentionRemovals} retention</span>
        </div>
      ) : null}
    </ListCard>
  );
}

/**
 * Deluno's schedulers say the same thing every time they find nothing to do, so
 * a raw feed is mostly "Finished checking Movies. Nothing else needs attention
 * right now." over and over. Identical messages collapse into one row carrying
 * how many times it happened.
 *
 * Collapsing only *adjacent* repeats was not enough: the movie and TV
 * schedulers interleave, so their no-ops alternate and never sit next to each
 * other. Grouping across the whole window turns six rows of nothing into two,
 * which is what leaves room for the events worth reading. Each group keeps the
 * position and timestamp of its most recent occurrence, so the feed still reads
 * newest first.
 */
function collapseRepeats(events: TickerEvent[]): TickerEvent[] {
  const collapsed: TickerEvent[] = [];
  const seen = new Map<string, TickerEvent>();

  for (const event of events) {
    const key = `${event.category}${event.message}`;
    const existing = seen.get(key);
    if (existing) {
      existing.repeats = (existing.repeats ?? 1) + 1;
      continue;
    }
    const copy = { ...event };
    seen.set(key, copy);
    collapsed.push(copy);
  }

  return collapsed;
}


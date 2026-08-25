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
import { StatusLed, type LedTone } from "../ui/status-led";
import { cn } from "../../lib/utils";
import type { ActivityEventItem } from "../../lib/api";
import { useVisibleInterval } from "../../hooks/use-visible-interval";
import { RealtimeGroups, useSignalREvent, useSignalRStatus } from "../../lib/use-signalr";

type Severity = "info" | "success" | "warning" | "error";

interface TickerEvent {
  id: string;
  message: string;
  category: string;
  severity: Severity;
  createdUtc: string;
  /** How many times in a row this exact message occurred. Absent means once. */
  repeats?: number;
}

/** A leading light carries the severity, so a bad minute reads as colour first. */
const SEVERITY_LED: Record<Severity, LedTone> = {
  info: "idle",
  success: "ok",
  warning: "warn",
  error: "danger"
};

export function ActivityTicker({ seed, limit = 10 }: { seed: ActivityEventItem[]; limit?: number }) {
  const status = useSignalRStatus();
  const [live, setLive] = useState<TickerEvent[]>([]);

  // "2m ago" has to keep counting. Without this the ages freeze at whatever
  // they were when the last event arrived, which on a quiet system is the
  // difference between "nothing has happened for an hour" and a lie.
  const [, setNow] = useState(() => Date.now());
  useVisibleInterval(() => setNow(Date.now()), 30_000);

  useSignalREvent("ActivityEventAdded", RealtimeGroups.Activity, (event) => {
    setLive((current) => {
      // The seed and the stream overlap at the moment the page loads, so an
      // event can arrive that is already in the list.
      if (current.some((item) => item.id === event.id)) return current;
      return [
        { id: event.id, message: event.message, category: event.category, severity: event.severity, createdUtc: event.createdUtc },
        ...current
      ].slice(0, limit);
    });
  });

  const seeded: TickerEvent[] = seed.map((item) => ({
    id: item.id,
    message: item.message,
    category: item.category,
    severity: (item.severity ?? "info") as Severity,
    createdUtc: item.createdUtc
  }));

  const seenLive = new Set(live.map((item) => item.id));
  const events = collapseRuns([...live, ...seeded.filter((item) => !seenLive.has(item.id))]).slice(0, limit);
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
                  {categoryLabel(event.category)} · {formatAgo(event.createdUtc)}
                </span>
              </span>
            </div>
          ))}
        </div>
      )}
    </ListCard>
  );
}

/**
 * Deluno's schedulers say the same thing every time they find nothing to do, so
 * a raw feed is mostly "Finished checking Movies. Nothing else needs attention
 * right now." over and over. Consecutive repeats of one message collapse into a
 * single row carrying how many times it happened, which turns ten rows of noise
 * into the handful of things that actually differ. Only *adjacent* repeats
 * collapse — a message that recurs after something else happened is genuinely
 * new information and keeps its own row.
 */
function collapseRuns(events: TickerEvent[]): TickerEvent[] {
  const collapsed: TickerEvent[] = [];

  for (const event of events) {
    const previous = collapsed.at(-1);
    if (previous && previous.message === event.message && previous.category === event.category) {
      previous.repeats = (previous.repeats ?? 1) + 1;
      continue;
    }
    collapsed.push({ ...event });
  }

  return collapsed;
}

/** Category slugs come from the backend as machine names; read them out. */
function categoryLabel(category: string) {
  const spaced = category.replace(/[_-]+/g, " ").trim();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : "General";
}

function formatAgo(value: string) {
  const then = new Date(value).getTime();
  if (Number.isNaN(then)) return "—";

  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (seconds < 10) return "just now";
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short" }).format(then);
}

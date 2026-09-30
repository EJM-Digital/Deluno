/**
 * Today's rhythm (#1096): what Deluno did for your titles, hour by hour, counted
 * from the stored activity and stacked by kind (searches, grabs, imports, the
 * processor, clean-ups, retention, subtitles, lists). The current hour grows as
 * new events arrive. Every bar is a real count; an hour with nothing is an empty
 * slot, never decoration. Routine background work (tasks queued and finished,
 * files read, artwork tidied) is not drawn: it is one quiet count beneath.
 * Each colour is named in words in the legend.
 */
import { useMemo } from "react";
import { CountUp } from "../../../components/ui/count-up";
import { cn } from "../../../lib/utils";
import { Panel, Plain } from "../parts";
import { KIND_ORDER, KIND_STYLE, isRoutine, type ActivityKind } from "./kinds";
import { addBuckets, bucketByHour, bucketsFromCounts, type HourCountRow, type RhythmRow } from "./series";
import { useNow } from "./hooks";

/** The kinds that are drawn: everything a person did or had done for them, never housekeeping. */
const SHOWN_KINDS = KIND_ORDER.filter((kind) => !isRoutine(kind));

/** How each kind reads in the legend, in the singular-free words a person uses. */
const LEGEND_WORDS: Partial<Record<ActivityKind, string>> = {
  search: "searches", grab: "grabbed", import: "imported", process: "processor", cleanup: "cleaned up", retention: "retention", subtitle: "subtitles", lists: "lists and requests", failed: "problems"
};

/**
 * `counts` are the day's real counts by the hour (read at `countedAt`); `rows`
 * are events to add: those that arrived since, or, with no counts, the newest
 * page, in which case `capped` says it is only the latest events and never
 * the day's total.
 */
export function Rhythm({ rows, counts, countedAt = 0, capped = false }: { rows: RhythmRow[]; counts?: HourCountRow[] | null; countedAt?: number; capped?: boolean }) {
  const now = useNow(60_000);
  const buckets = useMemo(() => {
    if (!counts) return bucketByHour(rows, now);
    return addBuckets(bucketsFromCounts(counts), bucketByHour(rows.filter((row) => Date.parse(row.createdUtc) > countedAt), now));
  }, [rows, counts, countedAt, now]);
  const showsCap = capped && !counts;
  const routine = buckets.reduce((sum, bucket) => sum + (bucket.counts.other ?? 0), 0);
  const drawn = (bucket: (typeof buckets)[number]) => bucket.total - (bucket.counts.other ?? 0);
  const total = buckets.reduce((sum, bucket) => sum + drawn(bucket), 0);
  const tallest = Math.max(1, ...buckets.map(drawn));
  const currentHour = new Date(now).getHours();
  const byKind = SHOWN_KINDS.map((kind) => ({ kind, count: buckets.reduce((sum, bucket) => sum + (bucket.counts[kind] ?? 0), 0) })).filter((entry) => entry.count > 0);

  return (
    <Panel
      title="Today"
      count={<span className="whitespace-nowrap">{showsCap ? "last 200 events" : total > 0 ? <CountUp value={total} format={(value) => `${Math.round(value).toLocaleString("en-AU")} ${Math.round(value) === 1 ? "event" : "events"}`} /> : "by the hour"}</span>}
      to="/activity"
      toLabel="Activity"
    >
      {total === 0 ? (
        <Plain>
          Quiet so far today. Each hour of Deluno&rsquo;s work shows here as it is done.
          {routine > 0 ? ` (${routine.toLocaleString("en-AU")} routine background events left out.)` : ""}
        </Plain>
      ) : (
        <div className="flex min-h-0 flex-1 flex-col px-[var(--card-pad-x)] pb-2 pt-2">
          <div role="img" aria-label={`${total} events today${showsCap ? " among the last 200" : ""}: ${byKind.map((entry) => `${entry.count} ${KIND_STYLE[entry.kind].label.toLowerCase()}`).join(", ")}.`} className="flex min-h-0 flex-1 items-end gap-[3px]">
            {buckets.map((bucket) => (
              <div key={bucket.hour} title={`${String(bucket.hour).padStart(2, "0")}:00, ${drawn(bucket)} ${drawn(bucket) === 1 ? "event" : "events"}`} className={cn("flex h-full min-w-0 flex-1 flex-col-reverse gap-px rounded-sm", bucket.hour === currentHour && "bg-primary/[0.07]")}>
                {SHOWN_KINDS.map((kind) => {
                  const count = bucket.counts[kind] ?? 0;
                  return count > 0 ? <span key={kind} className={cn("live-bar block w-full rounded-[2px]", KIND_STYLE[kind].fill)} style={{ height: `${(count / tallest) * 100}%` }} /> : null;
                })}
              </div>
            ))}
          </div>
          <div aria-hidden className="mt-1 flex justify-between text-[length:var(--type-caption)] text-muted-foreground tabular-nums">
            <span>0:00</span><span>6:00</span><span>12:00</span><span>18:00</span><span>24:00</span>
          </div>
          <ul className="mt-1.5 flex flex-wrap items-center gap-x-2.5 gap-y-0.5 text-[length:var(--type-caption)] text-muted-foreground">
            {byKind.map((entry) => (
              <li key={entry.kind} className="inline-flex items-center gap-1 whitespace-nowrap">
                <span aria-hidden className={cn("h-2 w-2 rounded-full", KIND_STYLE[entry.kind].fill)} />
                <span className="font-medium tabular-nums text-foreground">{entry.count.toLocaleString("en-AU")}</span>
                <span>{LEGEND_WORDS[entry.kind] ?? KIND_STYLE[entry.kind].label.toLowerCase()}</span>
              </li>
            ))}
            {routine > 0 ? (
              <li className="inline-flex items-center gap-1 whitespace-nowrap opacity-70" title="Tasks queued and finished, files read, artwork tidied: not drawn">
                <span aria-hidden className="h-2 w-2 rounded-full bg-muted-foreground/40" />
                <span className="font-medium tabular-nums">{routine.toLocaleString("en-AU")}</span>
                <span>routine</span>
              </li>
            ) : null}
          </ul>
        </div>
      )}
    </Panel>
  );
}

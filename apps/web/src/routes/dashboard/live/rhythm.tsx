/**
 * Today's rhythm (#1096): Deluno's own activity hour by hour, counted from the
 * stored activity and stacked by kind (searches, grabs, imports, clean-ups and
 * the rest). The current hour grows as new events arrive. Every bar is a real
 * count; an hour with nothing is an empty slot, never decoration.
 */
import { useMemo } from "react";
import { CountUp } from "../../../components/ui/count-up";
import { cn } from "../../../lib/utils";
import { Panel, Plain } from "../parts";
import { KIND_ORDER, KIND_STYLE } from "./kinds";
import { bucketByHour, type RhythmRow } from "./series";
import { useNow } from "./hooks";

export function Rhythm({ rows }: { rows: RhythmRow[] }) {
  const now = useNow(60_000);
  const buckets = useMemo(() => bucketByHour(rows, now), [rows, now]);
  const total = buckets.reduce((sum, bucket) => sum + bucket.total, 0);
  const tallest = Math.max(1, ...buckets.map((bucket) => bucket.total));
  const currentHour = new Date(now).getHours();
  const byKind = KIND_ORDER.map((kind) => ({ kind, count: buckets.reduce((sum, bucket) => sum + (bucket.counts[kind] ?? 0), 0) })).filter((entry) => entry.count > 0);

  return (
    <Panel
      title="Today"
      count={<span className="whitespace-nowrap">{total > 0 ? <CountUp value={total} format={(value) => `${Math.round(value).toLocaleString("en-AU")} events`} /> : "by the hour"}</span>}
      to="/activity"
      toLabel="Activity"
    >
      {total === 0 ? (
        <Plain>Quiet so far today. Each hour of Deluno&rsquo;s work shows here as it is done.</Plain>
      ) : (
        <div className="flex min-h-0 flex-1 flex-col px-[var(--card-pad-x)] pb-2 pt-2">
          <div role="img" aria-label={`${total} events today: ${byKind.map((entry) => `${entry.count} ${KIND_STYLE[entry.kind].label.toLowerCase()}`).join(", ")}.`} className="flex min-h-0 flex-1 items-end gap-[3px]">
            {buckets.map((bucket) => (
              <div key={bucket.hour} title={`${String(bucket.hour).padStart(2, "0")}:00, ${bucket.total} ${bucket.total === 1 ? "event" : "events"}`} className={cn("flex h-full min-w-0 flex-1 flex-col-reverse gap-px rounded-sm", bucket.hour === currentHour && "bg-primary/[0.07]")}>
                {KIND_ORDER.map((kind) => {
                  const count = bucket.counts[kind] ?? 0;
                  return count > 0 ? <span key={kind} className={cn("live-bar block w-full rounded-[2px]", KIND_STYLE[kind].fill)} style={{ height: `${(count / tallest) * 100}%` }} /> : null;
                })}
              </div>
            ))}
          </div>
          <div aria-hidden className="mt-1 flex justify-between text-[length:var(--type-caption)] text-muted-foreground tabular-nums">
            <span>0:00</span><span>6:00</span><span>12:00</span><span>18:00</span><span>24:00</span>
          </div>
          <ul className="mt-1.5 flex h-5 items-center gap-x-2.5 overflow-hidden whitespace-nowrap text-[length:var(--type-caption)] text-muted-foreground">
            {byKind.map((entry) => (
              <li key={entry.kind} title={KIND_STYLE[entry.kind].label} className="inline-flex items-center gap-1">
                <span aria-hidden className={cn("h-2 w-2 rounded-full", KIND_STYLE[entry.kind].fill)} />
                <span className="sr-only">{KIND_STYLE[entry.kind].label}</span>
                <span className="font-medium tabular-nums text-foreground">{entry.count.toLocaleString("en-AU")}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </Panel>
  );
}

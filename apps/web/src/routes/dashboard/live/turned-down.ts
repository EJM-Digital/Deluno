/**
 * Turned down today (#1098): every release Deluno looked at and did not take, by
 * a plain reason. The server counts them from the stored decisions; each search
 * that turns releases down also pushes them, so a bar moves the moment a decision
 * is made. A pushed release is added to the stored counts only if it is newer than
 * the counts, so a refresh never counts it twice.
 */
import type { LiveTurnedDown, LiveTurnedDownRelease, TurnedDownGroup } from "../../../lib/api/types/operations";
import type { ReleasesTurnedDownEvent } from "../../../lib/api/types/live-events";

/** The reasons, in the order the bars are drawn. */
export const REASON_ORDER: TurnedDownGroup[] = ["language", "notBetter", "tooBig", "fewSeeders", "fake", "other"];

/** The words for each reason: the same the server gives. */
export const REASON_LABEL: Record<TurnedDownGroup, string> = {
  language: "Not in a language you want",
  notBetter: "No better than what you have",
  tooBig: "Bigger than your size limit",
  fewSeeders: "Too few people sharing it",
  fake: "Fake or mislabelled",
  other: "Other reasons"
};

/** The colour of each reason's bar and dot: the app's own tokens. */
export const REASON_COLOUR: Record<TurnedDownGroup, string> = {
  language: "hsl(var(--info))",
  notBetter: "hsl(var(--muted-foreground))",
  tooBig: "hsl(var(--stage-grab))",
  fewSeeders: "hsl(var(--stage-search))",
  fake: "hsl(var(--warning))",
  other: "hsl(var(--stage-import))"
};

/** The most releases the list of newest turned-down releases keeps. */
export const LATEST_LIMIT = 20;

export interface TurnedDownView {
  total: number;
  reasons: Array<{ group: TurnedDownGroup; label: string; count: number }>;
  latest: LiveTurnedDownRelease[];
}

/** The stored counts with every pushed turn-down newer than them added on top. */
export function mergeTurnedDown(base: LiveTurnedDown | null | undefined, pushed: readonly ReleasesTurnedDownEvent[], asOfUtc: string | null | undefined): TurnedDownView {
  const since = Date.parse(asOfUtc ?? "");
  const counts = new Map<TurnedDownGroup, number>(REASON_ORDER.map((group) => [group, base?.reasons.find((reason) => reason.group === group)?.count ?? 0]));
  const added: LiveTurnedDownRelease[] = [];
  let total = base?.total ?? 0;
  for (const event of pushed) {
    const at = Date.parse(event.atUtc);
    if (Number.isFinite(since) && Number.isFinite(at) && at <= since) continue;
    for (const item of event.items) {
      counts.set(item.group, (counts.get(item.group) ?? 0) + 1);
      total += 1;
      added.push({
        release: item.release,
        group: item.group,
        label: item.label || REASON_LABEL[item.group],
        title: event.title ?? null,
        entityType: event.entityType === "movie" || event.entityType === "series" ? event.entityType : null,
        entityId: event.entityId ?? null,
        atUtc: event.atUtc
      });
    }
  }
  const latest = [...added.reverse(), ...(base?.latest ?? [])].slice(0, LATEST_LIMIT);
  return {
    total,
    reasons: REASON_ORDER.map((group) => ({ group, label: base?.reasons.find((reason) => reason.group === group)?.label ?? REASON_LABEL[group], count: counts.get(group) ?? 0 })),
    latest
  };
}

/** The width of a reason's bar: against the biggest reason, so the biggest is full and the rest are in proportion. */
export function barFraction(count: number, reasons: ReadonlyArray<{ count: number }>): number {
  const biggest = Math.max(0, ...reasons.map((reason) => reason.count));
  return biggest <= 0 ? 0 : Math.min(1, count / biggest);
}

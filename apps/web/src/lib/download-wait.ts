import type { DownloadHealthFinding, DownloadQueueItem } from "./api";
import { downloadQueueStatuses } from "./download-telemetry";

/**
 * One rule for "waiting" and "stuck" (#1449), read by the Live board, the bell and In progress alike.
 *
 * A download is stuck only when the stall rule has decided: a finding that counts strikes has reached its
 * limit (or the release is already refused), or the client itself says it has stalled with no rule watching.
 * Before that it is waiting, and says what for: the torrent's details, people sharing it, or the next check.
 * The server holds the strikes (`DownloadHealthStrikes.Counts`); this mirrors which kinds count.
 */

/** The platform-wide strike limit a finding outside the rule engine uses when the setting is not changed. */
export const DEFAULT_STRIKE_LIMIT = 3;

/** Mirrors `DownloadHealthStrikes.Counts`: the kinds that add a strike on each check. */
const STRIKE_KINDS = ["client-stalled", "no-progress", "too-slow", "low-peers", "dead-torrent", "tracker-unregistered", "dangerous-file", "client-failure", "magnet-metadata"];

export function isStrikeKind(kind: string) {
  return STRIKE_KINDS.includes(kind.toLowerCase());
}

function strikeLimit(finding: DownloadHealthFinding) {
  return Math.min(100, Math.max(1, finding.ruleStrikeLimit ?? DEFAULT_STRIKE_LIMIT));
}

function isIgnored(finding: DownloadHealthFinding, now: number) {
  return Boolean(finding.ignoredUntilUtc) && Date.parse(finding.ignoredUntilUtc as string) > now;
}

/** The stall rule has decided about this finding: its strikes reached the limit, or the release is already refused. */
export function findingDecided(finding: DownloadHealthFinding) {
  return finding.candidateBlocked || finding.strikeCount >= strikeLimit(finding);
}

export type StallDecision =
  /** The rule has decided (or the client says so with no rule watching): stuck. */
  | { state: "stuck"; finding: DownloadHealthFinding | null }
  /** A counting finding below its limit: waiting for the next check. */
  | { state: "waiting"; finding: DownloadHealthFinding; strike: number; limit: number }
  | null;

export function stallDecision(item: DownloadQueueItem, now = Date.now()): StallDecision {
  const findings = (item.healthFindings ?? []).filter((finding) => !isIgnored(finding, now));
  const counting = findings.filter((finding) => isStrikeKind(finding.kind));
  const decided = counting.find(findingDecided);
  if (decided) return { state: "stuck", finding: decided };
  // A finding that does not count strikes (a failed clean-up, a refused file) is not a stall: it is for a person.
  if (findings.some((finding) => !isStrikeKind(finding.kind))) return { state: "stuck", finding: findings.find((finding) => !isStrikeKind(finding.kind)) ?? null };
  if (counting.length > 0) {
    const finding = counting.reduce((most, next) => (next.strikeCount > most.strikeCount ? next : most));
    return { state: "waiting", finding, strike: Math.max(1, finding.strikeCount), limit: strikeLimit(finding) };
  }
  return item.status === downloadQueueStatuses.stalled ? { state: "stuck", finding: null } : null;
}

/** Whether the stall rule has decided this download is stuck. */
export function isStuck(item: DownloadQueueItem, now = Date.now()) {
  return stallDecision(item, now)?.state === "stuck";
}

function inWords(untilMs: number, now: number) {
  const seconds = Math.round((untilMs - now) / 1000);
  if (seconds <= 60) return "in under a minute";
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `in ${minutes} min`;
  return `in ${Math.round(minutes / 60)} h`;
}

function isTorrent(item: DownloadQueueItem) {
  const said = (item.protocol ?? "").toLowerCase();
  return !said.includes("usenet") && !said.includes("nzb");
}

/** What a download that has just started is waiting for, before any rule has looked: nothing for a download that is moving. */
function startingWords(item: DownloadQueueItem): string | null {
  if (item.status !== downloadQueueStatuses.downloading || (item.speedMbps ?? 0) > 0 || !isTorrent(item)) return null;
  if (item.sizeBytes <= 0) return "Getting the torrent's details";
  if (item.peers <= 0 && item.progress < 1) return "Looking for people sharing it";
  return null;
}

/**
 * The plain words for a download that is waiting, not stuck: what it waits for and, when a rule is counting,
 * which strike it is on and when the next check is. Null when there is nothing to wait for.
 */
export function waitingWords(item: DownloadQueueItem, now = Date.now()): string | null {
  const decision = stallDecision(item, now);
  if (decision?.state === "stuck") return null;
  const starting = startingWords(item);
  if (decision?.state === "waiting") {
    const { finding, strike, limit } = decision;
    const what = starting ?? (finding.kind === "low-peers" ? "Few people sharing it" : finding.kind === "too-slow" ? "Slow so far" : "No data yet");
    const next = finding.nextStrikeUtc && Date.parse(finding.nextStrikeUtc) > now ? `checking again ${inWords(Date.parse(finding.nextStrikeUtc), now)}` : "checking again soon";
    return `${what} · ${next} (strike ${strike} of ${limit})`;
  }
  return starting;
}

/**
 * What follows "Stuck" on a card: what Deluno will do next, from the rule that decided. A client that says it has
 * stalled with no rule watching just says it is not moving.
 */
export function stuckNextStep(item: DownloadQueueItem, now = Date.now()): string {
  const decision = stallDecision(item, now);
  const finding = decision?.state === "stuck" ? decision.finding : null;
  if (finding?.candidateBlocked) return "refused, finding another copy";
  if (finding && isStrikeKind(finding.kind)) return "not moving, waiting for you";
  if (finding) return "needs a look";
  return "not moving";
}

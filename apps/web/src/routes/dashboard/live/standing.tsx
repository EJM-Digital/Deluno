/**
 * Facts that stand beside what Deluno is doing (#1096).
 *
 * - The quality ring and the drive facts (today's space used and what retention
 *   will give back) are how things stand, so they are on System, inside its
 *   Library and Storage panels.
 */
import { CountUp } from "../../../components/ui/count-up";
import type { DashboardLiveExtras, DashboardSummary, DashboardVolume } from "../../../lib/api/types/operations";
import { formatBytes } from "../../../lib/utils";

/** The share of titles meeting their goal, as a whole percent; null when there is nothing in the library. */
export function qualityShare(summary: DashboardSummary | undefined): { percent: number; met: number; total: number; upgradable: number } | null {
  const library = summary?.library;
  if (!library) return null;
  const total = library.movies.total + library.shows.total;
  if (total <= 0) return null;
  const met = Math.min(total, library.movies.qualityMet + library.shows.qualityMet);
  return { percent: Math.round((met / total) * 100), met, total, upgradable: library.movies.upgradable + library.shows.upgradable };
}

const RING = 2 * Math.PI * 17;

/** The ring of how much of the library meets its quality goal, for System's Library panel (the upgradable count is its own line there). */
export function QualityRing({ summary }: { summary: DashboardSummary | undefined }) {
  const share = qualityShare(summary);
  if (!share) return null;
  return (
    <div className="flex items-center gap-3 border-b border-hairline px-(--card-pad-x) py-2">
      <span className="relative flex h-11 w-11 shrink-0 items-center justify-center" role="img" aria-label={`${share.percent} per cent of your library meets its quality goal`}>
        <svg viewBox="0 0 40 40" className="h-11 w-11 -rotate-90">
          <circle cx="20" cy="20" r="17" fill="none" strokeWidth="4" className="stroke-surface-3" />
          <circle cx="20" cy="20" r="17" fill="none" strokeWidth="4" strokeLinecap="round" className="live-ring-arc stroke-success" strokeDasharray={RING} strokeDashoffset={RING * (1 - share.percent / 100)} />
        </svg>
        <span className="absolute text-(length:--type-caption) font-semibold tabular-nums text-foreground">
          <CountUp value={share.percent} format={(value) => `${Math.round(value)}%`} />
        </span>
      </span>
      <span className="min-w-0 leading-tight">
        <span className="block text-(length:--type-body-sm) font-medium text-foreground">Meets its quality goal</span>
        <span className="block text-(length:--type-caption) text-muted-foreground">{share.met.toLocaleString("en-AU")} of {share.total.toLocaleString("en-AU")} titles</span>
      </span>
    </div>
  );
}

const fraction = (volume: DashboardVolume) => (volume.totalBytes && volume.freeBytes !== null ? Math.min(1, Math.max(0, 1 - volume.freeBytes / volume.totalBytes)) : null);

/** The drives that hold the library, most used first; with none marked as library, every drive Deluno reads. */
export function libraryDrives(summary: DashboardSummary | undefined): DashboardVolume[] {
  const volumes = summary?.storage?.volumes ?? [];
  const library = volumes.filter((volume) => volume.roles.some((role) => role.toLowerCase() === "library"));
  return [...(library.length > 0 ? library : volumes)].sort((a, b) => (fraction(b) ?? -1) - (fraction(a) ?? -1));
}

/** What today has done to the space: what was imported, and what retention would give back. Empty when neither is known. */
export function spaceToday(extras: DashboardLiveExtras | null | undefined): string {
  const parts: string[] = [];
  if (extras) parts.push(extras.importedBytesToday > 0 ? `Today's imports took ${formatBytes(extras.importedBytesToday)}.` : "Nothing imported yet today.");
  if (extras && extras.retentionReclaimBytes > 0) parts.push(`Retention can free ${formatBytes(extras.retentionReclaimBytes)}.`);
  return parts.join(" ");
}

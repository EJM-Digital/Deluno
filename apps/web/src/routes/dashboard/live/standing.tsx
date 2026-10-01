/**
 * The second row of the Right now band (#1096): three standing facts about
 * your library that move as Deluno works.
 *
 * - The quality ring: how much of the library meets its quality goal, filling as
 *   upgrades land (from the library counts in the summary).
 * - Protected you: how many bad releases were refused today and how many of
 *   them were fake or unplayable files, with the latest named (from the
 *   blocklist, stored rows only).
 * - Drives: each library drive's space, with what today's imports have taken and
 *   what retention will give back.
 */
import { ShieldCheck } from "lucide-react";
import { Link } from "react-router-dom";
import { CountUp } from "../../../components/ui/count-up";
import type { DashboardLiveExtras, DashboardSummary, DashboardVolume } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { formatSize } from "../downloads-kit";
import { ago } from "../format";
import { plainReason } from "./plain";

const LABEL = "text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.08em] text-muted-foreground";

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

export function QualityRing({ summary }: { summary: DashboardSummary | undefined }) {
  const share = qualityShare(summary);
  return (
    <section aria-label="Library quality" className="flex min-w-0 items-center gap-3 px-[var(--card-pad-x)] py-2">
      {share ? (
        <>
          <span className="relative flex h-12 w-12 shrink-0 items-center justify-center" role="img" aria-label={`${share.percent} per cent of your library meets its quality goal`}>
            <svg viewBox="0 0 40 40" className="h-12 w-12 -rotate-90">
              <circle cx="20" cy="20" r="17" fill="none" strokeWidth="4" className="stroke-surface-3" />
              <circle cx="20" cy="20" r="17" fill="none" strokeWidth="4" strokeLinecap="round" className="live-ring-arc stroke-success" strokeDasharray={RING} strokeDashoffset={RING * (1 - share.percent / 100)} />
            </svg>
            <span className="absolute text-[length:var(--type-caption)] font-semibold tabular-nums text-foreground">
              <CountUp value={share.percent} format={(value) => `${Math.round(value)}%`} />
            </span>
          </span>
          <span className="min-w-0">
            <span className={LABEL}>Library quality</span>
            <span className="block text-[length:var(--type-body-sm)] leading-tight text-foreground">meets its goal</span>
            <Link to="/movies?filter=upgrades" className="block truncate text-[length:var(--type-caption)] leading-tight text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
              {share.upgradable > 0 ? `${share.upgradable.toLocaleString("en-AU")} can still be upgraded` : "nothing left to upgrade"}
            </Link>
          </span>
        </>
      ) : (
        <span className="min-w-0">
          <span className={LABEL}>Library quality</span>
          <span className="block text-[length:var(--type-body-sm)] leading-tight text-muted-foreground">No titles to measure yet.</span>
        </span>
      )}
    </section>
  );
}

export function ProtectedYou({ extras, blocklistCount }: { extras: DashboardLiveExtras | null | undefined; blocklistCount: number | undefined }) {
  const guard = extras?.protected;
  const latest = guard?.latest[0];
  const said = latest ? plainReason(latest.reason)?.text ?? latest.reason : null;
  return (
    <section aria-label="Protected you" className="flex min-w-0 items-center gap-3 px-[var(--card-pad-x)] py-2">
      <span aria-hidden className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-success/15 text-success">
        <ShieldCheck className="h-5 w-5" />
      </span>
      <span className="min-w-0 flex-1">
        <Link to="/search-cycles/blocklist" aria-label="Blocklist" className={cn(LABEL, "hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring")}>Protected you today</Link>
        {guard ? (
          <>
            <span className="block text-[length:var(--type-body-sm)] leading-tight text-foreground">
              <span className="font-semibold tabular-nums"><CountUp value={guard.refusedToday} format={(value) => Math.round(value).toLocaleString("en-AU")} /></span> {guard.refusedToday === 1 ? "bad release" : "bad releases"} refused
              {guard.unsafeToday > 0 ? <>, <span className="font-semibold tabular-nums">{guard.unsafeToday.toLocaleString("en-AU")}</span> fake or unplayable {guard.unsafeToday === 1 ? "file" : "files"} blocked</> : null}
            </span>
            <span title={latest ? `${latest.release}: ${latest.reason}` : undefined} className="block truncate text-[length:var(--type-caption)] leading-tight text-muted-foreground">
              {latest ? `Latest: ${latest.release}, ${said}, ${ago(latest.blockedUtc)}` : blocklistCount ? `Nothing bad today. ${blocklistCount.toLocaleString("en-AU")} on your blocklist.` : "Nothing bad has needed refusing today."}
            </span>
          </>
        ) : (
          <span className="block text-[length:var(--type-body-sm)] leading-tight text-muted-foreground">{blocklistCount ? `${blocklistCount.toLocaleString("en-AU")} releases on your blocklist.` : "Nothing is on your blocklist."}</span>
        )}
      </span>
    </section>
  );
}

const fraction = (volume: DashboardVolume) => (volume.totalBytes && volume.freeBytes !== null ? Math.min(1, Math.max(0, 1 - volume.freeBytes / volume.totalBytes)) : null);

/** The drives that hold the library, most used first; with none marked as library, every drive Deluno reads. */
export function libraryDrives(summary: DashboardSummary | undefined): DashboardVolume[] {
  const volumes = summary?.storage?.volumes ?? [];
  const library = volumes.filter((volume) => volume.roles.some((role) => role.toLowerCase() === "library"));
  return [...(library.length > 0 ? library : volumes)].sort((a, b) => (fraction(b) ?? -1) - (fraction(a) ?? -1));
}

const MAX_DRIVES = 4;

export function DriveGauges({ summary, extras }: { summary: DashboardSummary | undefined; extras: DashboardLiveExtras | null | undefined }) {
  const drives = libraryDrives(summary);
  const shown = drives.slice(0, MAX_DRIVES);
  const today = extras?.importedBytesToday ?? 0;
  const reclaim = extras?.retentionReclaimBytes ?? 0;
  return (
    <section aria-label="Drive space" className="flex min-w-0 flex-col justify-center px-[var(--card-pad-x)] py-2">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3">
        <Link to="/system" aria-label="Storage" className={cn(LABEL, "hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring")}>Drive space</Link>
        <span className="text-[length:var(--type-caption)] text-muted-foreground">
          {extras ? <>today <span className="font-medium tabular-nums text-foreground">{today > 0 ? `+${formatSize(today)}` : "nothing new"}</span> imported</> : null}
          {reclaim > 0 ? <>{extras ? " · " : ""}retention can free <span className="font-medium tabular-nums text-success">{formatSize(reclaim)}</span></> : null}
        </span>
      </div>
      {shown.length === 0 ? (
        <p className="mt-0.5 text-[length:var(--type-body-sm)] text-muted-foreground">No drive has been read yet.</p>
      ) : (
        <ul className="mt-1 grid gap-x-4 gap-y-1" style={{ gridTemplateColumns: `repeat(${Math.min(shown.length, 4)}, minmax(0, 1fr))` }}>
          {shown.map((volume) => {
            const used = fraction(volume);
            const tone = used === null ? "bg-muted-foreground/40" : used >= 0.92 ? "bg-destructive" : used >= 0.8 ? "bg-warning" : "bg-primary";
            const words = !volume.available ? "cannot be read" : used === null ? "size unknown" : `${formatSize(volume.freeBytes ?? 0)} free of ${formatSize(volume.totalBytes ?? 0)}`;
            return (
              <li key={volume.id}>
                <Link to="/system" title={`${volume.label}: ${words}. ${volume.paths.join(", ")}`} aria-label={`${volume.label}: ${words}`} className="block rounded-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                  <span className="flex items-baseline justify-between gap-2 text-[length:var(--type-caption)] leading-tight">
                    <span className="min-w-0 truncate font-medium text-foreground">{volume.label}</span>
                    <span className="shrink-0 tabular-nums text-muted-foreground">{used === null ? "–" : <CountUp value={Math.round(used * 100)} format={(value) => `${Math.round(value)}% used`} />}</span>
                  </span>
                  <span aria-hidden className="mt-0.5 block h-1.5 overflow-hidden rounded-full bg-surface-3">
                    <span className={cn("live-gauge block h-full rounded-full", tone)} style={{ width: `${Math.round((used ?? 0) * 100)}%` }} />
                  </span>
                  <span className="block truncate text-[length:var(--type-micro)] leading-tight text-muted-foreground">{words}</span>
                </Link>
              </li>
            );
          })}
        </ul>
      )}
      {drives.length > shown.length ? <Link to="/system" className="mt-0.5 text-[length:var(--type-caption)] font-medium text-primary hover:underline">and {(drives.length - shown.length).toLocaleString("en-AU")} more</Link> : null}
    </section>
  );
}

import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { AlertTriangle, Clock, RefreshCw, Search, TrendingUp, Tv } from "lucide-react";
import {
  fetchJson,
  type LibraryAutomationStateItem,
  type SeriesWantedItem,
  type SeriesWantedSummary
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { JOB_STATUS } from "../lib/job-status-constants";
import { cn } from "../lib/utils";
import { Button } from "../components/ui/button";
import { Badge } from "../components/ui/badge";
import { EmptyState } from "../components/shell/empty-state";
import { GlassTile } from "../components/shell/page-hero";
import { toast } from "../components/shell/toaster";
import { RouteSkeleton } from "../components/shell/skeleton";
import { useState } from "react";
import { ArrowUpRight } from "lucide-react";

interface TvWantedLoaderData {
  wanted: SeriesWantedSummary;
  automation: LibraryAutomationStateItem[];
}

export async function tvWantedLoader(): Promise<TvWantedLoaderData> {
  const [wanted, automation] = await Promise.all([
    fetchJson<SeriesWantedSummary>("/api/series/wanted"),
    fetchJson<LibraryAutomationStateItem[]>("/api/library-automation").catch(() => [] as LibraryAutomationStateItem[])
  ]);
  return { wanted, automation };
}

export function TvWantedPage() {
  const data = useLoaderData() as TvWantedLoaderData | undefined;
  if (!data) return <RouteSkeleton />;

  const { wanted, automation } = data;
  const revalidator = useRevalidator();
  const [busyId, setBusyId] = useState<string | null>(null);

  const missing = wanted.recentItems.filter((i) => i.wantedStatus === "missing");
  const upgrades = wanted.recentItems.filter((i) => i.wantedStatus === "upgrade");
  const waiting = wanted.recentItems.filter((i) => i.wantedStatus === "waiting" || i.nextEligibleSearchUtc);

  const tvLibraries = automation.filter((a) => a.mediaType === "tv");

  async function handleSearchNow(libraryId: string) {
    setBusyId(libraryId);
    try {
      const res = await authedFetch(`/api/libraries/${libraryId}/search-now`, { method: "POST" });
      if (!res.ok) throw new Error("Search could not be queued.");
      toast.success("Search cycle queued — Deluno will work through the backlog.");
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Search request failed.");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      <div className="grid gap-4 sm:grid-cols-3">
        <SummaryTile icon={AlertTriangle} label="Missing" value={wanted.missingCount} tone="warn" tip="Shows or seasons Deluno has never found a release for" />
        <SummaryTile icon={TrendingUp} label="Ready for an upgrade" value={wanted.upgradeCount} tone="primary" tip="Shows already in your library that can be improved" />
        <SummaryTile icon={Clock} label="Waiting for retry" value={wanted.waitingCount} tone="neutral" tip="Searches that ran recently — Deluno will try again automatically" />
      </div>

      {tvLibraries.length > 0 && (
        <GlassTile>
          <div className="px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)]">
            <p className="text-[13px] font-semibold text-foreground">Search controls</p>
            <p className="mt-0.5 text-[12px] text-muted-foreground">
              Trigger an immediate search for a TV library. Deluno works through missing episodes and seasons automatically.
            </p>
            <div className="mt-3 flex flex-wrap gap-2">
              {tvLibraries.map((lib) => (
                <Button
                  key={lib.libraryId}
                  size="sm"
                  variant="outline"
                  className="gap-2"
                  disabled={busyId !== null || lib.status === JOB_STATUS.RUNNING}
                  onClick={() => void handleSearchNow(lib.libraryId)}
                  id={`tv-search-now-${lib.libraryId}`}
                >
                  <Search className="h-3.5 w-3.5" />
                  {lib.status === JOB_STATUS.RUNNING ? "Searching…" : `Search ${lib.libraryName}`}
                </Button>
              ))}
              <Button size="sm" variant="ghost" className="gap-2" onClick={() => revalidator.revalidate()}>
                <RefreshCw className="h-3.5 w-3.5" />
                Refresh
              </Button>
            </div>
          </div>
        </GlassTile>
      )}

      <GlassTile>
        <SectionHeader title="Missing shows" count={wanted.missingCount} description="These shows or seasons have never been found. Deluno searches automatically on your configured schedule." tone="warn" />
        {missing.length > 0 ? (
          <WantedTable items={missing} />
        ) : (
          <div className="px-[var(--tile-pad)] pb-[var(--tile-pad)]">
            <EmptyState size="sm" variant="custom" title="No missing TV shows" description="Every monitored show either has files or is in a retry window." />
          </div>
        )}
        {wanted.missingCount > missing.length && (
          <p className="border-t border-hairline px-[var(--tile-pad)] py-3 text-[12px] text-muted-foreground">
            Showing {missing.length} of {wanted.missingCount} — open a show to see its full episode inventory.
          </p>
        )}
      </GlassTile>

      <GlassTile>
        <SectionHeader title="Ready for an upgrade" count={wanted.upgradeCount} description="These shows are in your library but a better quality release is available. Deluno will grab it automatically." tone="primary" />
        {upgrades.length > 0 ? (
          <WantedTable items={upgrades} />
        ) : (
          <div className="px-[var(--tile-pad)] pb-[var(--tile-pad)]">
            <EmptyState size="sm" variant="custom" title="No upgrades waiting" description="All monitored shows are at or above their target quality." />
          </div>
        )}
      </GlassTile>

      {waiting.length > 0 && (
        <GlassTile>
          <SectionHeader title="In retry window" count={wanted.waitingCount} description="Deluno searched these recently and found nothing. It will try again automatically — no action needed." tone="neutral" />
          <WantedTable items={waiting} showRetryTime />
        </GlassTile>
      )}
    </div>
  );
}

function SummaryTile({ icon: Icon, label, value, tone, tip }: { icon: typeof Tv; label: string; value: number; tone: "warn" | "primary" | "neutral"; tip: string }) {
  const toneClass = { warn: "border-warning/25 bg-warning/8 text-warning", primary: "border-primary/25 bg-primary/8 text-primary", neutral: "border-hairline bg-muted/30 text-muted-foreground" }[tone];
  return (
    <div title={tip} className={cn("flex items-center gap-3 rounded-2xl border p-[var(--tile-pad)] shadow-card", toneClass)}>
      <span className={cn("flex h-10 w-10 shrink-0 items-center justify-center rounded-xl border", toneClass)}>
        <Icon className="h-5 w-5" strokeWidth={1.8} />
      </span>
      <span className="min-w-0">
        <span className="block font-display text-3xl font-bold leading-none tabular">{value}</span>
        <span className="mt-1 block text-[12px] font-semibold uppercase tracking-wide opacity-80">{label}</span>
      </span>
    </div>
  );
}

function SectionHeader({ title, count, description, tone }: { title: string; count: number; description: string; tone: "warn" | "primary" | "neutral" }) {
  const badgeVariant = tone === "warn" ? "destructive" : "default";
  return (
    <div className="flex flex-col gap-1 border-b border-hairline px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)]">
      <div className="flex items-center gap-2">
        <span className="font-display text-[15px] font-semibold text-foreground">{title}</span>
        <Badge variant={badgeVariant} className="text-[10px]">{count}</Badge>
      </div>
      <p className="text-[12px] leading-relaxed text-muted-foreground">{description}</p>
    </div>
  );
}

function WantedTable({ items, showRetryTime = false }: { items: SeriesWantedItem[]; showRetryTime?: boolean }) {
  return (
    <div className="divide-y divide-hairline">
      {items.map((item) => (
        <div key={`${item.seriesId}-${item.libraryId}`} className="grid gap-3 px-[var(--tile-pad)] py-3 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center">
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <Link to={`/tv/${item.seriesId}`} className="truncate font-semibold text-foreground hover:text-primary hover:underline">{item.title}</Link>
              {item.startYear ? <span className="text-[12px] tabular text-muted-foreground">{item.startYear}</span> : null}
              <WantedStatusBadge status={item.wantedStatus} />
            </div>
            <p className="mt-1 text-[12px] text-muted-foreground">{item.wantedReason}</p>
            {item.currentQuality && (
              <p className="mt-0.5 text-[11px] text-muted-foreground">
                Current: <span className="font-mono">{item.currentQuality}</span>
                {item.targetQuality ? <>{" → "}<span className="font-mono">{item.targetQuality}</span></> : null}
              </p>
            )}
            {showRetryTime && item.nextEligibleSearchUtc && (
              <p className="mt-0.5 text-[11px] text-muted-foreground">Next search: <span className="font-mono">{formatRelativeTime(item.nextEligibleSearchUtc)}</span></p>
            )}
            {item.lastSearchResult && <p className="mt-0.5 text-[11px] italic text-muted-foreground">Last result: {item.lastSearchResult}</p>}
          </div>
          <Link to={`/tv/${item.seriesId}`} className="inline-flex items-center gap-1 rounded-lg border border-hairline bg-surface-1 px-3 py-1.5 text-[12px] font-semibold text-foreground transition hover:border-primary/30 hover:bg-primary/5 hover:text-primary">
            Open <ArrowUpRight className="h-3 w-3" />
          </Link>
        </div>
      ))}
    </div>
  );
}

function WantedStatusBadge({ status }: { status: string }) {
  if (status === "missing") return <Badge variant="destructive" className="text-[9.5px]">Missing</Badge>;
  if (status === "upgrade") return <Badge variant="info" className="text-[9.5px]">Upgrade available</Badge>;
  if (status === "waiting") return <Badge variant="default" className="text-[9.5px]">Retry pending</Badge>;
  return <Badge variant="default" className="text-[9.5px]">{status}</Badge>;
}

function formatRelativeTime(utcString: string): string {
  const diff = new Date(utcString).getTime() - Date.now();
  if (diff <= 0) return "soon";
  const hours = Math.floor(diff / 3_600_000);
  const minutes = Math.floor((diff % 3_600_000) / 60_000);
  return hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`;
}

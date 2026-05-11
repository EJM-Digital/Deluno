import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { AlertTriangle, ArrowUpRight, Clock, Film, RefreshCw, Search, TrendingUp } from "lucide-react";
import {
  fetchJson,
  type LibraryAutomationStateItem,
  type MovieWantedItem,
  type MovieWantedSummary
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

interface WantedLoaderData {
  wanted: MovieWantedSummary;
  automation: LibraryAutomationStateItem[];
}

export async function moviesWantedLoader(): Promise<WantedLoaderData> {
  const [wanted, automation] = await Promise.all([
    fetchJson<MovieWantedSummary>("/api/movies/wanted"),
    fetchJson<LibraryAutomationStateItem[]>("/api/library-automation").catch(() => [] as LibraryAutomationStateItem[])
  ]);
  return { wanted, automation };
}

export function MoviesWantedPage() {
  const data = useLoaderData() as WantedLoaderData | undefined;
  if (!data) return <RouteSkeleton />;

  const { wanted, automation } = data;
  const revalidator = useRevalidator();
  const [busyId, setBusyId] = useState<string | null>(null);

  const missing = wanted.recentItems.filter((i) => i.wantedStatus === "missing");
  const upgrades = wanted.recentItems.filter((i) => i.wantedStatus === "upgrade");
  const waiting = wanted.recentItems.filter((i) => i.wantedStatus === "waiting" || i.nextEligibleSearchUtc);

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

  const movieLibraries = automation.filter((a) => a.mediaType === "movies");

  return (
    <div className="space-y-[var(--page-gap)]">
      {/* Plain-English summary for beginners */}
      <div className="grid gap-4 sm:grid-cols-3">
        <SummaryTile
          icon={AlertTriangle}
          label="Missing"
          value={wanted.missingCount}
          tone="warn"
          tip="Movies Deluno has never found a release for"
        />
        <SummaryTile
          icon={TrendingUp}
          label="Ready for an upgrade"
          value={wanted.upgradeCount}
          tone="primary"
          tip="Movies in your library that can be improved to a better quality"
        />
        <SummaryTile
          icon={Clock}
          label="Waiting for retry"
          value={wanted.waitingCount}
          tone="neutral"
          tip="Searches that ran recently — Deluno will try again automatically"
        />
      </div>

      {/* Library automation controls */}
      {movieLibraries.length > 0 && (
        <GlassTile>
          <div className="px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)]">
            <p className="text-[13px] font-semibold text-foreground">Search controls</p>
            <p className="mt-0.5 text-[12px] text-muted-foreground">
              Trigger an immediate search cycle for a library. Deluno will process the full wanted backlog and
              respect per-item cooldowns unless you are forcing a retry.
            </p>
            <div className="mt-3 flex flex-wrap gap-2">
              {movieLibraries.map((lib) => (
                <Button
                  key={lib.libraryId}
                  size="sm"
                  variant="outline"
                  className="gap-2"
                  disabled={busyId !== null || lib.status === JOB_STATUS.RUNNING}
                  onClick={() => void handleSearchNow(lib.libraryId)}
                  id={`search-now-${lib.libraryId}`}
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

      {/* Missing movies */}
      <GlassTile>
        <SectionHeader
          title="Missing movies"
          count={wanted.missingCount}
          description="These movies have never been found. Deluno searches automatically — or you can trigger a search from a movie's detail page."
          tone="warn"
        />
        {missing.length > 0 ? (
          <WantedTable items={missing} mediaType="movie" />
        ) : (
          <div className="px-[var(--tile-pad)] pb-[var(--tile-pad)]">
            <EmptyState size="sm" variant="custom" title="No missing movies" description="Every monitored movie either has a file or is waiting in a retry window." />
          </div>
        )}
        {wanted.missingCount > missing.length && (
          <p className="border-t border-hairline px-[var(--tile-pad)] py-3 text-[12px] text-muted-foreground">
            Showing {missing.length} of {wanted.missingCount} — open a movie to see its full history.
          </p>
        )}
      </GlassTile>

      {/* Upgrade-eligible */}
      <GlassTile>
        <SectionHeader
          title="Ready for an upgrade"
          count={wanted.upgradeCount}
          description="These movies are in your library but a better quality release exists. Deluno will grab it automatically, or you can trigger a search manually."
          tone="primary"
        />
        {upgrades.length > 0 ? (
          <WantedTable items={upgrades} mediaType="movie" />
        ) : (
          <div className="px-[var(--tile-pad)] pb-[var(--tile-pad)]">
            <EmptyState size="sm" variant="custom" title="No upgrades waiting" description="All monitored movies are already at or above their target quality." />
          </div>
        )}
      </GlassTile>

      {/* Waiting / cooldown */}
      {waiting.length > 0 && (
        <GlassTile>
          <SectionHeader
            title="In retry window"
            count={wanted.waitingCount}
            description="Deluno searched these recently and found nothing. It will try again automatically — no action needed unless you want to force a retry."
            tone="neutral"
          />
          <WantedTable items={waiting} mediaType="movie" showRetryTime />
        </GlassTile>
      )}
    </div>
  );
}

/* ── Shared sub-components ───────────────────────────────────────────── */

function SummaryTile({
  icon: Icon,
  label,
  value,
  tone,
  tip
}: {
  icon: typeof Film;
  label: string;
  value: number;
  tone: "warn" | "primary" | "neutral";
  tip: string;
}) {
  const toneClass = {
    warn: "border-warning/25 bg-warning/8 text-warning",
    primary: "border-primary/25 bg-primary/8 text-primary",
    neutral: "border-hairline bg-muted/30 text-muted-foreground"
  }[tone];

  return (
    <div
      title={tip}
      className={cn(
        "flex items-center gap-3 rounded-2xl border p-[var(--tile-pad)] shadow-card",
        toneClass
      )}
    >
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

function SectionHeader({
  title,
  count,
  description,
  tone
}: {
  title: string;
  count: number;
  description: string;
  tone: "warn" | "primary" | "neutral";
}) {
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

function WantedTable({
  items,
  mediaType,
  showRetryTime = false
}: {
  items: MovieWantedItem[];
  mediaType: "movie";
  showRetryTime?: boolean;
}) {
  return (
    <div className="divide-y divide-hairline">
      {items.map((item) => (
        <div
          key={`${item.movieId}-${item.libraryId}`}
          className="grid gap-3 px-[var(--tile-pad)] py-3 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center"
        >
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <Link
                to={`/movies/${item.movieId}`}
                className="truncate font-semibold text-foreground hover:text-primary hover:underline"
              >
                {item.title}
              </Link>
              {item.releaseYear ? (
                <span className="text-[12px] tabular text-muted-foreground">{item.releaseYear}</span>
              ) : null}
              <WantedStatusBadge status={item.wantedStatus} />
            </div>
            <p className="mt-1 text-[12px] text-muted-foreground">{item.wantedReason}</p>
            {item.currentQuality && (
              <p className="mt-0.5 text-[11px] text-muted-foreground">
                Current: <span className="font-mono">{item.currentQuality}</span>
                {item.targetQuality ? (
                  <>
                    {" → "}
                    <span className="font-mono">{item.targetQuality}</span>
                  </>
                ) : null}
              </p>
            )}
            {showRetryTime && item.nextEligibleSearchUtc && (
              <p className="mt-0.5 text-[11px] text-muted-foreground">
                Next search: <span className="font-mono">{formatRelativeTime(item.nextEligibleSearchUtc)}</span>
              </p>
            )}
            {item.lastSearchResult && (
              <p className="mt-0.5 text-[11px] italic text-muted-foreground">Last result: {item.lastSearchResult}</p>
            )}
          </div>
          <div className="flex items-center gap-2">
            <Link
              to={`/movies/${item.movieId}`}
              className="inline-flex items-center gap-1 rounded-lg border border-hairline bg-surface-1 px-3 py-1.5 text-[12px] font-semibold text-foreground transition hover:border-primary/30 hover:bg-primary/5 hover:text-primary"
              title="Open movie detail to search or force a grab"
            >
              Open
              <ArrowUpRight className="h-3 w-3" />
            </Link>
          </div>
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
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}

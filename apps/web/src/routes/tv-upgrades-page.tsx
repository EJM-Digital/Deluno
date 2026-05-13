import { Link, useLoaderData } from "react-router-dom";
import { ArrowUpRight, Clock3, ShieldCheck, TrendingUp } from "lucide-react";
import { fetchJson, type SeriesWantedItem, type SeriesWantedSummary } from "../lib/api";
import { Badge } from "../components/ui/badge";
import { GlassTile } from "../components/shell/page-hero";
import { EmptyState } from "../components/shell/empty-state";
import { RouteSkeleton } from "../components/shell/skeleton";

interface TvUpgradesLoaderData {
  wanted: SeriesWantedSummary;
}

export async function tvUpgradesLoader(): Promise<TvUpgradesLoaderData> {
  const wanted = await fetchJson<SeriesWantedSummary>("/api/series/wanted");
  return { wanted };
}

export function TvUpgradesPage() {
  const data = useLoaderData() as TvUpgradesLoaderData | undefined;
  if (!data) return <RouteSkeleton />;

  const upgradeItems = data.wanted.recentItems.filter((item) => item.wantedStatus === "upgrade");
  const waitingItems = data.wanted.recentItems.filter((item) => item.wantedStatus === "waiting");
  const strongCandidates = upgradeItems.filter((item) => item.currentQuality && item.targetQuality).length;

  return (
    <div className="space-y-[var(--page-gap)]">
      <div className="grid gap-4 sm:grid-cols-3">
        <MetricCard
          icon={TrendingUp}
          label="Upgrade candidates"
          value={upgradeItems.length}
          helper="Shows with available quality improvements"
        />
        <MetricCard
          icon={ShieldCheck}
          label="Clear replacement signal"
          value={strongCandidates}
          helper="Items with explicit current-to-target quality change"
        />
        <MetricCard
          icon={Clock3}
          label="Waiting retry"
          value={waitingItems.length}
          helper="Items still inside search cooldown"
        />
      </div>

      <GlassTile>
        <div className="border-b border-hairline px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)]">
          <h2 className="font-display text-[15px] font-semibold text-foreground">TV upgrades</h2>
          <p className="mt-1 text-[12px] leading-relaxed text-muted-foreground">
            Upgrade workflow is focused on replacement confidence so you can review why each show remains eligible.
          </p>
        </div>
        {upgradeItems.length > 0 ? (
          <div className="divide-y divide-hairline">
            {upgradeItems.map((item) => (
              <UpgradeRow key={`${item.seriesId}-${item.libraryId}`} item={item} />
            ))}
          </div>
        ) : (
          <div className="px-[var(--tile-pad)] py-[var(--tile-pad)]">
            <EmptyState
              size="sm"
              variant="custom"
              title="No TV upgrades pending"
              description="All monitored shows are already at or above target quality, or they are waiting on retry windows."
            />
          </div>
        )}
      </GlassTile>
    </div>
  );
}

function MetricCard({
  icon: Icon,
  label,
  value,
  helper
}: {
  icon: typeof TrendingUp;
  label: string;
  value: number;
  helper: string;
}) {
  return (
    <div className="rounded-2xl border border-hairline bg-card/85 p-[var(--tile-pad)] shadow-card">
      <div className="flex items-center gap-2 text-primary">
        <Icon className="h-4.5 w-4.5" />
        <span className="text-[11px] font-semibold uppercase tracking-[0.12em]">{label}</span>
      </div>
      <p className="mt-2 font-display text-3xl font-bold leading-none tabular text-foreground">{value}</p>
      <p className="mt-1 text-[12px] text-muted-foreground">{helper}</p>
    </div>
  );
}

function UpgradeRow({ item }: { item: SeriesWantedItem }) {
  return (
    <div className="grid gap-3 px-[var(--tile-pad)] py-3 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <Link to={`/tv/${item.seriesId}`} className="truncate font-semibold text-foreground hover:text-primary hover:underline">
            {item.title}
          </Link>
          {item.startYear ? <span className="text-[12px] tabular text-muted-foreground">{item.startYear}</span> : null}
          <Badge variant="info" className="text-[9.5px]">Upgrade</Badge>
        </div>
        <p className="mt-1 text-[12px] text-muted-foreground">{item.wantedReason}</p>
        <p className="mt-0.5 text-[11px] text-muted-foreground">
          Current: <span className="font-mono">{item.currentQuality ?? "unknown"}</span>{" "}
          <span className="text-foreground/70">{"->"}</span>{" "}
          Target: <span className="font-mono">{item.targetQuality ?? "not set"}</span>
        </p>
        {item.nextEligibleSearchUtc ? (
          <p className="mt-0.5 text-[11px] text-muted-foreground">Next retry: <span className="font-mono">{formatRelativeTime(item.nextEligibleSearchUtc)}</span></p>
        ) : null}
      </div>
      <Link
        to={`/tv/${item.seriesId}`}
        className="inline-flex items-center gap-1 rounded-lg border border-hairline bg-surface-1 px-3 py-1.5 text-[12px] font-semibold text-foreground transition hover:border-primary/30 hover:bg-primary/5 hover:text-primary"
      >
        Open
        <ArrowUpRight className="h-3 w-3" />
      </Link>
    </div>
  );
}

function formatRelativeTime(utcString: string): string {
  const diff = new Date(utcString).getTime() - Date.now();
  if (diff <= 0) return "soon";
  const hours = Math.floor(diff / 3_600_000);
  const minutes = Math.floor((diff % 3_600_000) / 60_000);
  if (hours > 0) return `${hours}h ${minutes}m`;
  return `${minutes}m`;
}

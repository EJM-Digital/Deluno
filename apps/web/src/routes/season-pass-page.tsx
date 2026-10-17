/**
 * Season Pass — every series' seasons, across the whole TV library, with
 * episode counts and monitor state, and bulk-toggle at the season or series
 * level (#791). Sonarr's Season Pass grid, for the same reason Sonarr has
 * one: bulk-monitoring inside a single series' page cannot answer "which
 * seasons, across my whole library, am I not tracking".
 *
 * Contracts: GET /api/series/season-pass; PUT /api/series/season-pass/monitoring.
 */
import { useMemo, useState } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import { ArrowLeft, LoaderCircle } from "lucide-react";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";
import { fetchJson, type SeriesSeasonPassItem } from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { Button } from "../components/ui/button";
import { ListGroupHeader } from "../components/ui/media-type-split";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Switch } from "../components/ui/switch";
import { SummaryStrip } from "../components/ui/summary-strip";
import { toast } from "../components/shell/toaster";

interface SeasonPassLoaderData {
  seasons: SeriesSeasonPassItem[];
  /** The list could not be read: the page says so instead of reading "No seasons yet". */
  unreadable?: boolean;
}

export async function seasonPassLoader(): Promise<SeasonPassLoaderData> {
  try {
    return { seasons: await fetchJson<SeriesSeasonPassItem[]>("/api/series/season-pass") };
  } catch {
    return { seasons: [], unreadable: true };
  }
}

/** How a season/series' monitor state reads at a glance - never all three at once. */
export function seasonMonitorState(episodeCount: number, monitoredCount: number): "all" | "some" | "none" {
  if (episodeCount === 0 || monitoredCount === 0) return "none";
  if (monitoredCount >= episodeCount) return "all";
  return "some";
}

/** Groups the flat season-pass list into one entry per series, in title order. */
export function groupSeasonPassBySeries(seasons: SeriesSeasonPassItem[]): SeriesSeasonPassItem[][] {
  const groups = new Map<string, SeriesSeasonPassItem[]>();
  for (const season of seasons) {
    groups.set(season.seriesId, [...(groups.get(season.seriesId) ?? []), season]);
  }
  return [...groups.values()]
    .map((group) => [...group].sort((left, right) => left.seasonNumber - right.seasonNumber))
    .sort((left, right) => left[0].seriesTitle.localeCompare(right[0].seriesTitle));
}

export function SeasonPassPage() {
  const data = useLoaderData() as SeasonPassLoaderData;
  const revalidator = useRevalidator();
  // Which episodes are held or wanted changes as shows and episodes do (#1179).
  const readAgain = useCoalescedRevalidate(() => revalidator.revalidate(), 500);
  useSignalREvent("SeriesChanged", RealtimeGroups.Dashboard, readAgain);
  useSignalREvent("EpisodeChanged", RealtimeGroups.Dashboard, readAgain);
  const [busy, setBusy] = useState<string | null>(null);

  const { seasons } = data;
  const bySeries = useMemo(() => groupSeasonPassBySeries(seasons), [seasons]);

  const totalEpisodes = seasons.reduce((sum, season) => sum + season.episodeCount, 0);
  const totalMonitored = seasons.reduce((sum, season) => sum + season.monitoredCount, 0);
  const seasonsFullyMonitored = seasons.filter((season) => seasonMonitorState(season.episodeCount, season.monitoredCount) === "all").length;
  const seasonsUnmonitored = seasons.filter((season) => seasonMonitorState(season.episodeCount, season.monitoredCount) === "none").length;

  async function setMonitoring(scopes: { seriesId: string; seasonNumber: number | null }[], monitored: boolean, key: string) {
    setBusy(key);
    try {
      const response = await authedFetch("/api/series/season-pass/monitoring", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ scopes, monitored })
      });
      if (!response.ok) throw new Error("season-pass-monitoring-failed");
      revalidator.revalidate();
    } catch {
      toast.error("Monitoring could not be changed.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        actions={
          <Button asChild type="button" variant="outline">
            <Link to="/tv">
              <ArrowLeft className="h-4 w-4" />
              All TV
            </Link>
          </Button>
        }
      />

      <SummaryStrip
        cells={[
          { label: "Series", value: bySeries.length },
          { label: "Seasons", value: seasons.length },
          { label: "Episodes", value: totalEpisodes },
          { label: "Monitored", value: totalMonitored, help: `of ${totalEpisodes} episodes` },
          { label: "Seasons fully monitored", value: seasonsFullyMonitored },
          { label: "Seasons unmonitored", value: seasonsUnmonitored }
        ]}
      />

      <ListCard title="Season Pass" count={`${seasons.length} season${seasons.length === 1 ? "" : "s"}`}>
        {data.unreadable ? (
          <ListEmpty
            title="The seasons could not be read"
            description="Deluno could not get the list of seasons just now. Nothing was changed."
            actions={<Button type="button" variant="outline" onClick={() => revalidator.revalidate()}>Try again</Button>}
          />
        ) : seasons.length === 0 ? (
          <ListEmpty
            title="No seasons yet"
            description="A show needs its episode catalogue synced before its seasons appear here."
          />
        ) : (
          <ListTable
            listId="season-pass"
            columns={[
              { label: "Season" },
              { label: "Episodes" },
              { label: "Monitored", width: LIST_TRACK.status },
              { label: "Toggle", width: LIST_TRACK.action, align: "end", mobile: true }
            ]}
            chevron={false}
          >
            {bySeries.map((group) => {
              const seriesId = group[0].seriesId;
              const seriesEpisodeCount = group.reduce((sum, season) => sum + season.episodeCount, 0);
              const seriesMonitoredCount = group.reduce((sum, season) => sum + season.monitoredCount, 0);
              const seriesState = seasonMonitorState(seriesEpisodeCount, seriesMonitoredCount);
              return (
                <div key={seriesId}>
                  <ListGroupHeader
                    label={group[0].seriesTitle}
                    detail={`${seriesMonitoredCount}/${seriesEpisodeCount} monitored`}
                    actions={
                      <label className="inline-flex items-center gap-2 text-xs text-muted-foreground">
                        Whole series
                        <Switch
                          aria-label={`Monitor all of ${group[0].seriesTitle}`}
                          checked={seriesState === "all"}
                          disabled={busy !== null}
                          onCheckedChange={(checked) => void setMonitoring([{ seriesId, seasonNumber: null }], checked, `series-${seriesId}`)}
                        />
                        {busy === `series-${seriesId}` ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : null}
                      </label>
                    }
                  />
                  {group.map((season) => {
                    const state = seasonMonitorState(season.episodeCount, season.monitoredCount);
                    const key = `${seriesId}-${season.seasonNumber}`;
                    return (
                      <ListRow key={key}>
                        <ListNameCell name={season.seasonNumber === 0 ? "Specials" : `Season ${season.seasonNumber}`} />
                        <ListCell primary={String(season.episodeCount)} />
                        <ListCell primary={`${season.monitoredCount}/${season.episodeCount}${state === "some" ? " (partial)" : ""}`} />
                        <div role="cell" className="flex justify-end">
                          <Switch
                            aria-label={`Monitor ${group[0].seriesTitle} ${season.seasonNumber === 0 ? "specials" : `season ${season.seasonNumber}`}`}
                            checked={state === "all"}
                            disabled={busy !== null}
                            onCheckedChange={(checked) => void setMonitoring([{ seriesId, seasonNumber: season.seasonNumber }], checked, key)}
                          />
                          {busy === key ? <LoaderCircle className="ml-2 h-3.5 w-3.5 animate-spin" /> : null}
                        </div>
                      </ListRow>
                    );
                  })}
                </div>
              );
            })}
          </ListTable>
        )}
      </ListCard>
    </div>
  );
}

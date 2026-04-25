import { useMemo, useState } from "react";
import { Link, useLoaderData } from "react-router-dom";
import { CalendarDays, Clock3, Radar, RefreshCcw, Tv2 } from "lucide-react";
import {
  fetchJson,
  type MovieWantedSummary,
  type SeriesInventoryDetail,
  type SeriesListItem,
  type SeriesWantedSummary
} from "../lib/api";
import { KpiCard } from "../components/app/kpi-card";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { EmptyState } from "../components/shell/empty-state";
import { Stagger, StaggerItem } from "../components/shell/motion";

interface CalendarItem {
  id: string;
  kind: "episode" | "retry";
  title: string;
  subtitle: string;
  detail: string;
  startsAt: string;
  tone: "primary" | "warning" | "info";
  href: string;
}

interface CalendarLoaderData {
  items: CalendarItem[];
}

export async function calendarLoader(): Promise<CalendarLoaderData> {
  try {
    const [series, seriesWanted, movieWanted] = await Promise.all([
      fetchJson<SeriesListItem[]>("/api/series"),
      fetchJson<SeriesWantedSummary>("/api/series/wanted"),
      fetchJson<MovieWantedSummary>("/api/movies/wanted")
    ]);

    const inventory = await Promise.all(
      series.map((item) =>
        fetchJson<SeriesInventoryDetail>(`/api/series/${item.id}/inventory`).catch(() => null)
      )
    );

    const episodeItems: CalendarItem[] = inventory
      .filter((item): item is SeriesInventoryDetail => item !== null)
      .flatMap((detail) =>
        detail.episodes
          .filter((episode) => episode.airDateUtc)
          .map((episode) => ({
            id: episode.episodeId,
            kind: "episode" as const,
            title: detail.title,
            subtitle: `S${String(episode.seasonNumber).padStart(2, "0")}E${String(episode.episodeNumber).padStart(2, "0")}`,
            detail: episode.title ?? "Upcoming episode",
            startsAt: episode.airDateUtc!,
            tone: episode.hasFile ? "info" : episode.wantedStatus === "missing" ? "warning" : "primary",
            href: `/tv/${detail.seriesId}`
          }))
      );

    const retryItems: CalendarItem[] = [
      ...seriesWanted.recentItems
        .filter((item) => item.nextEligibleSearchUtc)
        .map((item) => ({
          id: `series-retry-${item.seriesId}`,
          kind: "retry" as const,
          title: item.title,
          subtitle: "TV search retry",
          detail: item.wantedReason,
          startsAt: item.nextEligibleSearchUtc!,
          tone: "warning" as const,
          href: `/tv/${item.seriesId}`
        })),
      ...movieWanted.recentItems
        .filter((item) => item.nextEligibleSearchUtc)
        .map((item) => ({
          id: `movie-retry-${item.movieId}`,
          kind: "retry" as const,
          title: item.title,
          subtitle: "Movie search retry",
          detail: item.wantedReason,
          startsAt: item.nextEligibleSearchUtc!,
          tone: "info" as const,
          href: `/movies/${item.movieId}`
        }))
    ];

    const items = [...episodeItems, ...retryItems]
      .sort((left, right) => new Date(left.startsAt).getTime() - new Date(right.startsAt).getTime())
      .slice(0, 48);

    return { items };
  } catch {
    return { items: [] };
  }
}

export function CalendarPage() {
  const loaderData = useLoaderData() as CalendarLoaderData | undefined;
  const items = loaderData?.items ?? [];
  const [weekOffset, setWeekOffset] = useState(0);
  const week = useMemo(() => buildWeek(weekOffset), [weekOffset]);

  const itemsThisWeek = useMemo(() => {
    return items.filter((item) => {
      const time = new Date(item.startsAt).getTime();
      return time >= week.start.getTime() && time < week.end.getTime();
    });
  }, [items, week]);

  const grouped = useMemo(() => {
    return week.days.map((day) => ({
      ...day,
      items: itemsThisWeek.filter((item) => isSameDay(new Date(item.startsAt), day.date))
    }));
  }, [itemsThisWeek, week.days]);

  const nextSevenDays = items.filter((item) => {
    const time = new Date(item.startsAt).getTime();
    const now = Date.now();
    return time >= now && time < now + 1000 * 60 * 60 * 24 * 7;
  });

  const episodeCount = itemsThisWeek.filter((item) => item.kind === "episode").length;
  const retryCount = itemsThisWeek.filter((item) => item.kind === "retry").length;

  return (
    <div className="space-y-[var(--page-gap)]">
      <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div>
          <p className="text-sm text-muted-foreground">Release planning</p>
          <h1 className="font-display text-3xl font-semibold text-foreground sm:text-4xl">
            Calendar
          </h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Upcoming episodes and retry windows in one schedule view.
          </p>
        </div>
        <div className="flex gap-2">
          <Button variant="outline" onClick={() => setWeekOffset((value) => value - 1)}>
            Previous
          </Button>
          <Button variant="outline" onClick={() => setWeekOffset(0)}>
            Today
          </Button>
          <Button variant="outline" onClick={() => setWeekOffset((value) => value + 1)}>
            Next
          </Button>
        </div>
      </div>

      <Stagger className="grid gap-[var(--grid-gap)] md:grid-cols-2 xl:grid-cols-4">
        <StaggerItem>
          <KpiCard
            label="This week"
            value={String(itemsThisWeek.length)}
            icon={CalendarDays}
            meta="Episodes and retry windows inside the visible week."
            sparkline={[4, 6, 5, 8, 7, 9, 8, 10, 11, 9, 8, 7, 9, 10, 12]}
          />
        </StaggerItem>
        <StaggerItem>
          <KpiCard
            label="Episodes"
            value={String(episodeCount)}
            icon={Tv2}
            meta="Upcoming or recently aired TV inventory with air dates."
            sparkline={[1, 2, 3, 2, 4, 3, 5, 6, 5, 7, 6, 5, 6, 7, 8]}
          />
        </StaggerItem>
        <StaggerItem>
          <KpiCard
            label="Retries"
            value={String(retryCount)}
            icon={RefreshCcw}
            meta="Search retry windows scheduled from wanted-state timing."
            sparkline={[2, 2, 1, 3, 2, 4, 3, 4, 5, 4, 3, 2, 3, 4, 4]}
          />
        </StaggerItem>
        <StaggerItem>
          <KpiCard
            label="Next 7 days"
            value={String(nextSevenDays.length)}
            icon={Radar}
            meta="Everything Deluno expects to care about in the next week."
            sparkline={[5, 5, 6, 7, 7, 8, 9, 10, 9, 11, 10, 12, 11, 12, 13]}
          />
        </StaggerItem>
      </Stagger>

      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[minmax(0,1.3fr)_minmax(360px,0.92fr)] 2xl:grid-cols-[minmax(0,1.55fr)_minmax(420px,0.72fr)]">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between gap-4">
            <div>
              <CardTitle>{week.label}</CardTitle>
              <CardDescription>
                Week-at-a-glance scheduling for TV air dates and Deluno retry windows.
              </CardDescription>
            </div>
            <Badge variant="info">{itemsThisWeek.length} scheduled</Badge>
          </CardHeader>
          <CardContent className="grid gap-[var(--grid-gap)] lg:grid-cols-7">
            {grouped.map((day) => (
              <div key={day.key} className="rounded-xl border border-hairline bg-surface-1 p-3">
                <div className="border-b border-hairline pb-3">
                  <p className="text-[11px] uppercase tracking-[0.18em] text-muted-foreground">
                    {day.weekday}
                  </p>
                  <p className="mt-1 tabular text-lg font-semibold text-foreground">
                    {day.dayNumber}
                  </p>
                </div>
                <div className="mt-3 space-y-3">
                  {day.items.length ? (
                    day.items.map((item) => (
                      <Link
                        key={item.id}
                        to={item.href}
                        className="block rounded-xl border border-hairline bg-card p-3 transition hover:bg-surface-2"
                      >
                        <div className="flex items-center justify-between gap-2">
                          <Badge variant={badgeForTone(item.tone)}>{item.subtitle}</Badge>
                          <span className="tabular text-[11px] text-muted-foreground">
                            {formatTime(item.startsAt)}
                          </span>
                        </div>
                        <p className="mt-2 text-sm font-medium text-foreground">{item.title}</p>
                        <p className="mt-1 text-xs text-muted-foreground">{item.detail}</p>
                      </Link>
                    ))
                  ) : (
                    <p className="text-sm text-muted-foreground">Nothing scheduled.</p>
                  )}
                </div>
              </div>
            ))}
          </CardContent>
        </Card>

        <div className="space-y-[var(--page-gap)]">
          <Card>
            <CardHeader>
              <CardTitle>Upcoming agenda</CardTitle>
              <CardDescription>
                Chronological list of the next dated events Deluno knows about.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              {items.slice(0, 12).map((item) => (
                <Link
                  key={item.id}
                  to={item.href}
                  className="flex items-start gap-3 rounded-xl border border-hairline bg-surface-1 px-3 py-3 transition hover:bg-surface-2"
                >
                  <span className={toneDot(item.tone)} />
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-2">
                      <p className="text-sm font-medium text-foreground">{item.title}</p>
                      <Badge variant={badgeForTone(item.tone)}>{item.subtitle}</Badge>
                    </div>
                    <p className="mt-1 text-sm text-muted-foreground">{item.detail}</p>
                    <p className="mt-1 inline-flex items-center gap-1 text-xs text-muted-foreground">
                      <Clock3 className="h-3.5 w-3.5" />
                      {formatDateTime(item.startsAt)}
                    </p>
                  </div>
                </Link>
              ))}
              {!items.length ? (
                <EmptyState
                  size="sm"
                  variant="custom"
                  title="Nothing on the agenda"
                  description="Upcoming air dates and retry windows will surface here as soon as Deluno has them."
                />
              ) : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Planning notes</CardTitle>
              <CardDescription>
                What this calendar is currently driven by inside Deluno.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3 text-sm text-muted-foreground">
              <p>Episode cards come from TV inventory entries with real `airDateUtc` values.</p>
              <p>Retry entries come from movie and TV wanted-state `nextEligibleSearchUtc` timing.</p>
              <p>
                As Deluno gets richer release metadata and movie availability dates, this page can expand into a fuller planner without changing the shell.
              </p>
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}

function buildWeek(offset: number) {
  const today = new Date();
  const dayIndex = today.getDay();
  const mondayOffset = (dayIndex + 6) % 7;
  const start = new Date(today);
  start.setHours(0, 0, 0, 0);
  start.setDate(start.getDate() - mondayOffset + offset * 7);

  const days = Array.from({ length: 7 }, (_, index) => {
    const date = new Date(start);
    date.setDate(start.getDate() + index);
    return {
      key: date.toISOString(),
      date,
      weekday: new Intl.DateTimeFormat(undefined, { weekday: "short" }).format(date),
      dayNumber: new Intl.DateTimeFormat(undefined, { day: "numeric" }).format(date)
    };
  });

  const end = new Date(start);
  end.setDate(start.getDate() + 7);

  return {
    start,
    end,
    days,
    label: `${formatMonthDay(start)} – ${formatMonthDay(new Date(end.getTime() - 86400000))}`
  };
}

function formatMonthDay(date: Date) {
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" }).format(date);
}

function isSameDay(left: Date, right: Date) {
  return (
    left.getFullYear() === right.getFullYear() &&
    left.getMonth() === right.getMonth() &&
    left.getDate() === right.getDate()
  );
}

function formatTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}

function badgeForTone(tone: CalendarItem["tone"]): "default" | "success" | "warning" | "destructive" | "info" {
  switch (tone) {
    case "primary":
      return "info";
    case "warning":
      return "warning";
    default:
      return "default";
  }
}

function toneDot(tone: CalendarItem["tone"]) {
  switch (tone) {
    case "primary":
      return "mt-1.5 h-2.5 w-2.5 rounded-full bg-primary shadow-[0_0_8px_hsl(var(--primary)/0.45)]";
    case "warning":
      return "mt-1.5 h-2.5 w-2.5 rounded-full bg-warning shadow-[0_0_8px_hsl(var(--warning)/0.35)]";
    default:
      return "mt-1.5 h-2.5 w-2.5 rounded-full bg-info shadow-[0_0_8px_hsl(var(--info)/0.35)]";
  }
}

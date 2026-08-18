/**
 * Schedule — what lands when.
 *
 * TV air dates and film release dates come from the provider catalogue, so this
 * page is a window query rather than a scan: it asks the API for the visible
 * range instead of pulling every episode of every show and slicing. Before the
 * catalogue existed nothing had a date and this page could only ever be empty.
 *
 * Contracts: GET /api/series/calendar?from&to, GET /api/movies/calendar?from&to.
 */
import { useMemo, useState } from "react";
import { useLoaderData, useNavigate, useRevalidator } from "react-router-dom";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { fetchJson } from "../lib/api";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ListGroupHeader } from "../components/ui/media-type-split";
import {
  LIST_TRACK,
  ListCard,
  ListCell,
  ListEmpty,
  ListNameCell,
  ListRow,
  ListTable
} from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { RouteSkeleton } from "../components/shell/skeleton";
import { SegmentedControl } from "../components/ui/segmented-control";
import { SummaryStrip } from "../components/ui/summary-strip";
import { wantedStatusPresentation } from "../lib/media-status-presentation";

interface SeriesCalendarEpisode {
  episodeId: string;
  seriesId: string;
  seriesTitle: string;
  posterUrl: string | null;
  seasonNumber: number;
  episodeNumber: number;
  title: string | null;
  airDateUtc: string;
  hasFile: boolean;
  monitored: boolean;
  wantedStatus: string;
}

interface MovieCalendarEntry {
  movieId: string;
  title: string;
  releaseYear: number | null;
  posterUrl: string | null;
  kind: "inCinemas" | "digital" | "physical";
  date: string;
  hasFile: boolean;
  monitored: boolean;
}

interface CalendarLoaderData {
  episodes: SeriesCalendarEpisode[];
  movies: MovieCalendarEntry[];
}

/** The page loads a generous window once, then filters it as you page around. */
const WINDOW_DAYS_BACK = 45;
const WINDOW_DAYS_FORWARD = 120;

export async function calendarLoader(): Promise<CalendarLoaderData> {
  const from = new Date();
  from.setDate(from.getDate() - WINDOW_DAYS_BACK);
  const to = new Date();
  to.setDate(to.getDate() + WINDOW_DAYS_FORWARD);

  const range = `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`;
  const movieRange = `from=${isoDate(from)}&to=${isoDate(to)}`;

  const [episodes, movies] = await Promise.all([
    fetchJson<SeriesCalendarEpisode[]>(`/api/series/calendar?${range}`).catch(() => []),
    fetchJson<MovieCalendarEntry[]>(`/api/movies/calendar?${movieRange}`).catch(() => [])
  ]);

  return { episodes, movies };
}

type Scope = "week" | "month";

interface CalendarEntry {
  id: string;
  date: Date;
  name: string;
  sub: string;
  kindLabel: string;
  detail: string;
  status: { label: string; tone: "ok" | "warn" | "info" | "muted" };
  href: string;
}

export function CalendarPage() {
  const loaderData = useLoaderData() as CalendarLoaderData | undefined;
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const [scope, setScope] = useState<Scope>("month");
  const [offset, setOffset] = useState(0);

  if (!loaderData) return <RouteSkeleton />;

  const entries = useMemo(() => buildEntries(loaderData), [loaderData]);
  const range = useMemo(() => buildRange(scope, offset), [scope, offset]);

  const visible = useMemo(
    () => entries.filter((entry) => entry.date >= range.start && entry.date < range.end),
    [entries, range]
  );

  const byDay = useMemo(() => {
    const groups = new Map<string, CalendarEntry[]>();
    for (const entry of visible) {
      const key = isoDate(entry.date);
      groups.set(key, [...(groups.get(key) ?? []), entry]);
    }
    return [...groups.entries()].sort(([left], [right]) => left.localeCompare(right));
  }, [visible]);

  const now = new Date();
  const stillToCome = visible.filter((entry) => entry.date >= now).length;
  const alreadyHere = visible.filter((entry) => entry.date < now).length;
  const episodeCount = visible.filter((entry) => entry.id.startsWith("episode:")).length;
  const movieCount = visible.length - episodeCount;

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar
        left={
          <SegmentedControl<Scope>
            aria-label="Range"
            className="w-auto"
            value={scope}
            onValueChange={(next) => {
              setScope(next);
              setOffset(0);
            }}
            options={[
              { value: "week", label: "Week" },
              { value: "month", label: "Month" }
            ]}
          />
        }
        actions={
          <>
            <span className="mr-1 text-[length:var(--type-body-sm)] font-medium text-foreground">{range.label}</span>
            <Button type="button" variant="outline" size="icon" aria-label={`Previous ${scope}`} onClick={() => setOffset((value) => value - 1)}>
              <ChevronLeft className="h-4 w-4" />
            </Button>
            <Button type="button" variant="outline" onClick={() => setOffset(0)}>
              Today
            </Button>
            <Button type="button" variant="outline" size="icon" aria-label={`Next ${scope}`} onClick={() => setOffset((value) => value + 1)}>
              <ChevronRight className="h-4 w-4" />
            </Button>
          </>
        }
      />

      <SummaryStrip
        cells={[
          { label: "In this range", value: visible.length, help: range.label },
          { label: "Still to come", value: stillToCome, help: "has not happened yet" },
          { label: "Already here", value: alreadyHere, help: "aired or released" },
          { label: "Episodes", value: episodeCount, help: "TV air dates" },
          { label: "Films", value: movieCount, help: "cinema, digital and disc" }
        ]}
      />

      <ListCard
        title="Schedule"
        count={visible.length ? `${byDay.length} ${byDay.length === 1 ? "day" : "days"} with something on` : undefined}
        actions={
          <Button type="button" size="sm" variant="outline" onClick={() => revalidator.revalidate()} disabled={revalidator.state !== "idle"}>
            Refresh
          </Button>
        }
      >
        {visible.length === 0 ? (
          <ListEmpty
            title={entries.length ? `Nothing scheduled in ${range.label}` : "Nothing has a date yet"}
            description={
              entries.length
                ? "Move to another week or month, or widen the range."
                : "Air dates and release dates come from the metadata provider. Link a show or film to its provider record and its schedule appears here."
            }
          />
        ) : (
          <ListTable
            columns={[
              { label: "What" },
              { label: "Kind", mobile: true },
              { label: "When" },
              { label: "Detail", width: "minmax(0,1.4fr)" },
              { label: "Status", width: LIST_TRACK.status }
            ]}
          >
            {byDay.map(([day, dayEntries]) => (
              <div key={day}>
                <ListGroupHeader
                  label={formatDayLabel(new Date(`${day}T00:00:00`))}
                  detail={`${dayEntries.length} ${dayEntries.length === 1 ? "thing" : "things"}`}
                />
                {dayEntries.map((entry) => (
                  <ListRow key={entry.id} onClick={() => navigate(entry.href)}>
                    <ListNameCell name={entry.name} sub={entry.sub} />
                    <ListCell primary={entry.kindLabel} mobile />
                    <ListCell primary={formatTime(entry.date)} />
                    <ListCell primary={entry.detail} />
                    <ListCell>
                      <Chip tone={entry.status.tone}>{entry.status.label}</Chip>
                    </ListCell>
                  </ListRow>
                ))}
              </div>
            ))}
          </ListTable>
        )}
      </ListCard>
    </div>
  );
}

/* -------------------------------------------------------------- helpers */

function buildEntries(data: CalendarLoaderData): CalendarEntry[] {
  const episodes = data.episodes.map<CalendarEntry>((episode) => {
    const status = wantedStatusPresentation(episode.wantedStatus);
    return {
      id: `episode:${episode.episodeId}`,
      date: new Date(episode.airDateUtc),
      name: episode.seriesTitle,
      sub: `S${String(episode.seasonNumber).padStart(2, "0")}E${String(episode.episodeNumber).padStart(2, "0")}`,
      kindLabel: "Episode",
      detail: episode.title ?? "Episode title pending",
      status: episode.hasFile
        ? { label: "On disk", tone: "ok" as const }
        : { label: status.label, tone: status.tone },
      href: `/tv/${episode.seriesId}`
    };
  });

  const movies = data.movies.map<CalendarEntry>((movie) => ({
    id: `movie:${movie.movieId}:${movie.kind}:${movie.date}`,
    date: new Date(`${movie.date}T00:00:00`),
    name: movie.title,
    sub: movie.releaseYear ? String(movie.releaseYear) : "Film",
    kindLabel: movieKindLabel(movie.kind),
    detail: movieKindDetail(movie.kind),
    status: movie.hasFile
      ? { label: "On disk", tone: "ok" as const }
      : movie.monitored
        ? { label: "Watching for it", tone: "info" as const }
        : { label: "Not monitored", tone: "muted" as const },
    href: `/movies/${movie.movieId}`
  }));

  return [...episodes, ...movies].sort((left, right) => left.date.getTime() - right.date.getTime());
}

function movieKindLabel(kind: MovieCalendarEntry["kind"]) {
  switch (kind) {
    case "inCinemas":
      return "In cinemas";
    case "digital":
      return "Digital";
    default:
      return "Disc";
  }
}

function movieKindDetail(kind: MovieCalendarEntry["kind"]) {
  switch (kind) {
    case "inCinemas":
      return "Reaches cinemas — not obtainable yet";
    case "digital":
      return "Digital release";
    default:
      return "Physical release";
  }
}

function buildRange(scope: Scope, offset: number) {
  const anchor = new Date();
  anchor.setHours(0, 0, 0, 0);

  if (scope === "week") {
    // Monday-first, which is how a week reads for scheduling.
    const weekday = (anchor.getDay() + 6) % 7;
    const start = new Date(anchor);
    start.setDate(anchor.getDate() - weekday + offset * 7);
    const end = new Date(start);
    end.setDate(start.getDate() + 7);
    return { start, end, label: formatWeekLabel(start, end) };
  }

  const start = new Date(anchor.getFullYear(), anchor.getMonth() + offset, 1);
  const end = new Date(start.getFullYear(), start.getMonth() + 1, 1);
  return {
    start,
    end,
    label: new Intl.DateTimeFormat(undefined, { month: "long", year: "numeric" }).format(start)
  };
}

function formatWeekLabel(start: Date, end: Date) {
  const last = new Date(end);
  last.setDate(end.getDate() - 1);
  const sameMonth = start.getMonth() === last.getMonth();
  const startPart = new Intl.DateTimeFormat(undefined, { day: "numeric", month: sameMonth ? undefined : "short" }).format(start);
  const endPart = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short" }).format(last);
  return `${startPart} – ${endPart}`;
}

function formatDayLabel(date: Date) {
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const diff = Math.round((date.getTime() - today.getTime()) / 86_400_000);
  if (diff === 0) return "Today";
  if (diff === 1) return "Tomorrow";
  if (diff === -1) return "Yesterday";
  return new Intl.DateTimeFormat(undefined, { weekday: "long", day: "numeric", month: "long" }).format(date);
}

function formatTime(date: Date) {
  // Air dates are date-only, stored at midnight UTC. Read the UTC components:
  // reading local ones turns "no time known" into a confident "10:00" outside UTC.
  return date.getUTCHours() === 0 && date.getUTCMinutes() === 0
    ? "All day"
    : new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(date);
}

function isoDate(date: Date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

/**
 * Schedule — what lands when.
 *
 * TV air dates and movie release dates come from the provider catalogue, so this
 * page is a window query rather than a scan: it asks the API for the visible
 * range instead of pulling every episode of every show and slicing. Before the
 * catalogue existed nothing had a date and this page could only ever be empty.
 *
 * Contracts: GET /api/series/calendar?from&to, GET /api/movies/calendar?from&to.
 */
import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { defineSources, sourceLoader, useRouteSources } from "../lib/route-cache";
import { useCoalescedRevalidate } from "../hooks/use-live-interval";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";
import { RouteSourceGate } from "../components/shell/route-source-gate";
import { CalendarPlus, ChevronLeft, ChevronRight, SlidersHorizontal } from "lucide-react";
import { fetchJson } from "../lib/api";
import { cn } from "../lib/utils";
import { CalendarSubscribeDrawer } from "../components/app/calendar-subscribe-drawer";
import { Button } from "../components/ui/button";
import { Card } from "../components/ui/card";
import { Drawer, DrawerFooter, DrawerSection } from "../components/ui/drawer";
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
import { SegmentedControl } from "../components/ui/segmented-control";
import { SummaryStrip } from "../components/ui/summary-strip";
import { SwitchRow } from "../components/ui/switch";
import { TitleMarkDot, TitleMarkLabel } from "../components/ui/title-mark";
import type { TitleMarkInput } from "../components/ui/title-mark";
import {
  formatCalendarWeekHeader,
  formatLongDate,
  formatMonth,
  formatRangeDate,
  formatTime as formatPreferenceTime,
  hasBroadcastTime,
  type CalendarFirstDay,
  type DisplayPreferences,
  useDisplayPreferences
} from "../lib/display-preferences";
import type { MediaType } from "../lib/media-types";

interface SeriesCalendarEpisode {
  episodeId: string;
  seriesId: string;
  seriesTitle: string;
  posterUrl: string | null;
  seasonNumber: number;
  episodeNumber: number;
  title: string | null;
  airDateUtc: string;
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
  monitored: boolean;
  /** The stored wanted status, so the calendar shows the mark the card shows. */
  wantedStatus: string;
}

interface CalendarLoaderData {
  episodes: SeriesCalendarEpisode[];
  movies: MovieCalendarEntry[];
}

/** The page loads a generous window once, then filters it as you page around. */
const WINDOW_DAYS_BACK = 45;
const WINDOW_DAYS_FORWARD = 120;

const calendarSourceSets = new Map<string, ReturnType<typeof buildCalendarSources>>();

function buildCalendarSources() {
  const from = new Date();
  from.setDate(from.getDate() - WINDOW_DAYS_BACK);
  const to = new Date();
  to.setDate(to.getDate() + WINDOW_DAYS_FORWARD);

  const range = `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`;
  const movieRange = `from=${isoDate(from)}&to=${isoDate(to)}`;

  return defineSources({
    episodes: {
      key: ["src", "calendar-episodes", isoDate(from), isoDate(to)],
      label: "Upcoming episodes",
      fetch: () => fetchJson<SeriesCalendarEpisode[]>(`/api/series/calendar?${range}`).catch(() => [] as SeriesCalendarEpisode[])
    },
    movies: {
      key: ["src", "calendar-movies", isoDate(from), isoDate(to)],
      label: "Upcoming movies",
      fetch: () => fetchJson<MovieCalendarEntry[]>(`/api/movies/calendar?${movieRange}`).catch(() => [] as MovieCalendarEntry[])
    }
  });
}

/** The window moves with the day, so each day has its own set of sources. */
function calendarSources() {
  const today = isoDate(new Date());
  let sources = calendarSourceSets.get(today);
  if (!sources) {
    sources = buildCalendarSources();
    calendarSourceSets.clear();
    calendarSourceSets.set(today, sources);
  }
  return sources;
}

export function calendarLoader() {
  return sourceLoader(calendarSources())();
}

/**
 * The range a scope covers. Day and Agenda (#786) join Week and Month; both
 * always render as a list rather than a 7-column grid, because a single day
 * or a flowing window of upcoming things has no week shape to draw.
 */
type Scope = "day" | "week" | "month" | "agenda";
type View = "grid" | "list";

/** How many days ahead an Agenda page covers — Radarr's own default window. */
const AGENDA_WINDOW_DAYS = 14;

/** These two scopes are never shown as a calendar grid. */
function isListOnlyScope(scope: Scope): boolean {
  return scope === "day" || scope === "agenda";
}

interface CalendarOptions {
  /** Highlights an entry still short of its quality cutoff — Radarr's own options toggle. */
  highlightCutoffUnmet: boolean;
  /** Which movie release kinds appear on the calendar at all. */
  showInCinemas: boolean;
  showDigital: boolean;
  showPhysical: boolean;
}

const DEFAULT_CALENDAR_OPTIONS: CalendarOptions = {
  highlightCutoffUnmet: false,
  showInCinemas: true,
  showDigital: true,
  showPhysical: true
};

/**
 * Schedule keeps what you chose when you leave it and come back (James, 2 Oct 2026): the range, Calendar or List and
 * the options for good, and where you stepped to for as long as the tab is open, so a fresh visit opens on today.
 */
const SCHEDULE_CHOICES_KEY = "deluno-schedule-choices";
const SCHEDULE_OFFSET_KEY = "deluno-schedule-offset";
const SCOPES: readonly Scope[] = ["day", "week", "month", "agenda"];

interface ScheduleChoices {
  scope: Scope;
  view: View;
  options: CalendarOptions;
}

function readScheduleChoices(): ScheduleChoices {
  const fallback: ScheduleChoices = { scope: "month", view: "grid", options: DEFAULT_CALENDAR_OPTIONS };
  try {
    const raw = JSON.parse(window.localStorage.getItem(SCHEDULE_CHOICES_KEY) ?? "null") as Partial<ScheduleChoices> | null;
    if (!raw || typeof raw !== "object") return fallback;
    return {
      scope: SCOPES.includes(raw.scope as Scope) ? (raw.scope as Scope) : fallback.scope,
      view: raw.view === "list" || raw.view === "grid" ? raw.view : fallback.view,
      options: { ...DEFAULT_CALENDAR_OPTIONS, ...(raw.options && typeof raw.options === "object" ? raw.options : {}) }
    };
  } catch {
    return fallback;
  }
}

function readScheduleOffset(scope: Scope): number {
  try {
    const raw = JSON.parse(window.sessionStorage.getItem(SCHEDULE_OFFSET_KEY) ?? "null") as { scope?: string; offset?: number } | null;
    return raw && raw.scope === scope && Number.isInteger(raw.offset) ? raw.offset! : 0;
  } catch {
    return 0;
  }
}

interface CalendarEntry {
  id: string;
  date: Date;
  name: string;
  sub: string;
  kindLabel: string;
  detail: string;
  /**
   * Which shelf this row's mark belongs to.
   *
   * The calendar is the one list that mixes both media, so it cannot assume a
   * medium the way a movie list can — and a mark drawn without one falls back
   * to the page-text palette instead of the bar surfaces its card uses. The
   * same title would then be one red here and another on its poster.
   */
  mediaType: MediaType;
  /**
   * The mark, the same one the title's own card carries.
   *
   * This was a hand-written `{ label, tone }`: an aired episode with no file
   * read blue "Missing" here and red Missing on its poster, and a monitored
   * movie with no file read blue "Watching for it" — a phrase that appears
   * nowhere else in Deluno. Two vocabularies for one state (#302).
   */
  mark: TitleMarkInput;
  /** An episode that has not aired yet: it reads "Airs <date>", never "Missing" (#1179). */
  unaired?: boolean;
  href: string;
  /** Which release this row is, for the movie release-type toggles. Absent for an episode. */
  movieKind?: MovieCalendarEntry["kind"];
}

export function CalendarPage() {
  const state = useRouteSources(calendarSources());
  // A film, show or episode changing from any path moves what the calendar holds (a new air date, a
  // title added or removed, a file arriving), so the window is read again as it is announced (#1179).
  const readAgain = useCoalescedRevalidate(() => { void state.refetch(); }, 500);
  useSignalREvent("MovieChanged", RealtimeGroups.Dashboard, readAgain);
  useSignalREvent("SeriesChanged", RealtimeGroups.Dashboard, readAgain);
  useSignalREvent("EpisodeChanged", RealtimeGroups.Dashboard, readAgain);
  return (
    <RouteSourceGate state={state}>
      {() => <CalendarPageBody data={state.data} />}
    </RouteSourceGate>
  );
}

function CalendarPageBody({ data: loaderData }: { data: CalendarLoaderData }) {
  const navigate = useNavigate();
  const [initial] = useState(readScheduleChoices);
  const [scope, setScope] = useState<Scope>(initial.scope);
  const [view, setView] = useState<View>(initial.view);
  const [offset, setOffset] = useState(() => readScheduleOffset(initial.scope));
  const [subscribeOpen, setSubscribeOpen] = useState(false);
  const [optionsOpen, setOptionsOpen] = useState(false);
  const [options, setOptions] = useState<CalendarOptions>(initial.options);
  useEffect(() => {
    try {
      window.localStorage.setItem(SCHEDULE_CHOICES_KEY, JSON.stringify({ scope, view, options }));
    } catch {
      // A browser that keeps nothing still shows the choice; it is forgotten on leaving.
    }
  }, [scope, view, options]);
  useEffect(() => {
    try {
      window.sessionStorage.setItem(SCHEDULE_OFFSET_KEY, JSON.stringify({ scope, offset }));
    } catch {
      // As above.
    }
  }, [scope, offset]);
  const { preferences } = useDisplayPreferences();

  // Day and Agenda have no week shape to draw, so they always read as the
  // list — the segmented Calendar/List control only ever meant something for
  // Week and Month (#786).
  const effectiveView: View = isListOnlyScope(scope) ? "list" : view;

  const entries = useMemo(() => buildEntries(loaderData), [loaderData]);
  const range = useMemo(() => buildRange(scope, offset, preferences), [offset, preferences, scope]);

  const visible = useMemo(
    () =>
      entries.filter((entry) => {
        if (entry.date < range.start || entry.date >= range.end) return false;
        if (entry.movieKind === "inCinemas" && !options.showInCinemas) return false;
        if (entry.movieKind === "digital" && !options.showDigital) return false;
        if (entry.movieKind === "physical" && !options.showPhysical) return false;
        return true;
      }),
    [entries, range, options.showDigital, options.showInCinemas, options.showPhysical]
  );

  const byDay = useMemo(() => {
    const groups = new Map<string, CalendarEntry[]>();
    for (const entry of visible) {
      const key = isoDate(entry.date);
      groups.set(key, [...(groups.get(key) ?? []), entry]);
    }
    return [...groups.entries()].sort(([left], [right]) => left.localeCompare(right));
  }, [visible]);

  // A calendar wants whole weeks, so the grid runs Monday-to-Sunday across the
  // range's edges and dims the days that belong to the neighbouring month.
  const weeks = useMemo(() => buildWeeks(range, byDay, preferences.calendarFirstDayOfWeek), [byDay, preferences.calendarFirstDayOfWeek, range]);
  const weekHeaders = weeks[0]?.map((cell) => formatCalendarWeekHeader(cell.date, preferences)) ?? [];

  const now = new Date();
  const stillToCome = visible.filter((entry) => entry.date >= now).length;
  const alreadyHere = visible.filter((entry) => entry.date < now).length;
  const episodeCount = visible.filter((entry) => entry.id.startsWith("episode:")).length;
  const movieCount = visible.length - episodeCount;

  // What is shown and when: these steer the card, so they sit in its header, and both views carry them in the same
  // place, so switching between Calendar and List never moves a control out from under the cursor. Subscribe does
  // something, so it goes on the title line with every page's buttons (James, 2 Oct 2026). The calendar follows
  // the library by itself, so there is no Refresh (#1179).
  const cardActions = (
    <>
      {isListOnlyScope(scope) ? null : (
        <SegmentedControl<View>
          aria-label="View"
          className="mr-1 w-auto"
          value={effectiveView}
          onValueChange={setView}
          options={[
            { value: "grid", label: "Calendar" },
            { value: "list", label: "List" }
          ]}
        />
      )}
      <Button type="button" size="sm" variant="outline" className="mr-1" onClick={() => setOptionsOpen(true)}>
        <SlidersHorizontal className="h-3.5 w-3.5" />
        Options
      </Button>
      <Button type="button" size="sm" variant="outline" className="w-(--control-height-sm) px-0" aria-label={`Previous ${scope}`} onClick={() => setOffset((value) => value - 1)}>
        <ChevronLeft className="h-4 w-4" />
      </Button>
      <Button type="button" size="sm" variant="outline" onClick={() => setOffset(0)}>
        Today
      </Button>
      <Button type="button" size="sm" variant="outline" className="w-(--control-height-sm) px-0" aria-label={`Next ${scope}`} onClick={() => setOffset((value) => value + 1)}>
        <ChevronRight className="h-4 w-4" />
      </Button>
    </>
  );

  const nothingHere = entries.length
    ? `Nothing is scheduled in ${range.label}. Step to another ${scope} with the arrows, or switch the range above.`
    : "Nothing has a date yet. Air dates and release dates come from the metadata provider — link a show or movie to its provider record and its schedule appears here.";

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar
        sections={{
          label: "Range",
          value: scope,
          onValueChange: (next) => {
            setScope(next as Scope);
            setOffset(0);
          },
          options: [
            { value: "day", label: "Day" },
            { value: "week", label: "Week" },
            { value: "month", label: "Month" },
            { value: "agenda", label: "Agenda" }
          ]
        }}
        actions={
          <>
            <Button type="button" variant="outline" onClick={() => setSubscribeOpen(true)}>
              <CalendarPlus className="h-4 w-4" />
              Subscribe
            </Button>
          </>
        }
      />

      <SummaryStrip
        cells={[
          { label: "In this range", value: visible.length, help: range.label },
          { label: "Still to come", value: stillToCome, help: "has not happened yet" },
          { label: "Already out", value: alreadyHere, help: "aired or released" },
          { label: "Episodes", value: episodeCount, help: "TV air dates" },
          { label: "Movies", value: movieCount, help: "cinema, digital and disc" }
        ]}
      />

      {effectiveView === "grid" ? (
        <Card as="section" className="dark:border-white/[0.07]">
          <header className="flex min-h-(--list-header-height) flex-wrap items-center gap-x-3 gap-y-2 border-b border-hairline px-(--card-pad-x) py-2">
            <h2 className="text-(length:--type-card-title) font-semibold leading-none text-foreground">{range.label}</h2>
            <span className="text-(length:--type-caption) text-muted-foreground">
              {visible.length} {visible.length === 1 ? "thing" : "things"}
            </span>
            <div className="ml-auto flex max-w-full flex-wrap items-center gap-2">{cardActions}</div>
          </header>

          <div className="grid grid-cols-7 border-b border-hairline bg-surface-2/40">
            {weekHeaders.map((day, index) => (
              <span
                key={`${day}-${index}`}
                className="px-2 py-2 text-center text-(length:--type-micro) font-semibold uppercase tracking-widest text-muted-foreground"
              >
                {day}
              </span>
            ))}
          </div>

          <div className="grid grid-cols-7">
            {weeks.flat().map((cell) => (
              <div
                key={cell.key}
                className={cn(
                  "min-h-[112px] border-b border-r border-hairline p-1.5 last:border-r-0 nth-[7n]:border-r-0",
                  !cell.inRange && "bg-surface-2/30",
                  cell.isToday && "bg-primary/6"
                )}
              >
                <div className="flex items-baseline justify-between gap-1 px-1">
                  <span
                    className={cn(
                      "text-(length:--type-caption) tabular-nums",
                      cell.isToday ? "font-semibold text-primary" : cell.inRange ? "text-foreground" : "text-muted-foreground"
                    )}
                  >
                    {cell.date.getDate()}
                  </span>
                  {cell.entries.length > 2 ? (
                    <span className="text-(length:--type-micro) text-muted-foreground">{cell.entries.length}</span>
                  ) : null}
                </div>

                <div className="mt-1 grid gap-1">
                  {cell.entries.slice(0, 3).map((entry) => (
                    <button
                      key={entry.id}
                      type="button"
                      onClick={() => navigate(entry.href)}
                      title={`${entry.name} · ${entry.detail}`}
                      className={cn(
                        "w-full whitespace-normal break-words rounded-[6px] border px-1.5 py-1 text-left text-(length:--type-micro) leading-tight transition-colors",
                        "focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring",
                        entry.mark.monitored
                          ? "border-hairline bg-surface-2 text-foreground hover:bg-surface-3"
                          : "border-dashed border-hairline bg-transparent text-muted-foreground opacity-70 hover:bg-surface-2",
                        options.highlightCutoffUnmet && entry.mark.wantedStatus === "upgrade" && "ring-1 ring-inset ring-warning/70"
                      )}
                    >
                      {/* The dot carries the colour, as it does everywhere else.
                          The chip used to be tinted end to end in the tone, which
                          is a second way of saying the same thing and left a
                          month grid where every colour meant something different
                          from the shelf it links to. */}
                      <TitleMarkDot item={entry.mark} size={7} className="align-middle md:mr-1" />
                      {!entry.mark.monitored ? <span className="sr-only">Not monitored. </span> : null}
                      {/* A phone's day cell is about 37px wide: only the dot shows there, and the words are read out and one tap away. */}
                      <span className="sr-only md:not-sr-only"><span className="font-semibold">{entry.sub}</span> {entry.name}</span>
                    </button>
                  ))}
                  {cell.entries.length > 3 ? (
                    <span className="px-1.5 text-(length:--type-micro) text-muted-foreground">
                      +{cell.entries.length - 3} more
                    </span>
                  ) : null}
                </div>
              </div>
            ))}
          </div>

          {visible.length === 0 ? (
            // A grid of empty cells explains nothing on its own.
            <p className="border-t border-hairline px-(--card-pad-x) py-3 text-(length:--type-caption) leading-relaxed text-muted-foreground">
              {nothingHere}
            </p>
          ) : null}
        </Card>
      ) : (
      <ListCard
        title={range.label}
        count={visible.length ? `${byDay.length} ${byDay.length === 1 ? "day" : "days"} with something on` : undefined}
        actions={cardActions}
      >
        {visible.length === 0 ? (
          <ListEmpty
            title={entries.length ? `Nothing scheduled in ${range.label}` : "Nothing has a date yet"}
            description={nothingHere}
          />
        ) : (
          <ListTable
            listId="calendar"
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
                  label={formatDayLabel(new Date(`${day}T00:00:00`), preferences)}
                  detail={`${dayEntries.length} ${dayEntries.length === 1 ? "thing" : "things"}`}
                />
                {dayEntries.map((entry) => (
                  <ListRow
                    key={entry.id}
                    onClick={() => navigate(entry.href)}
                    className={
                      options.highlightCutoffUnmet && entry.mark.wantedStatus === "upgrade"
                        ? "bg-warning/6"
                        : undefined
                    }
                  >
                    <ListNameCell name={entry.name} sub={entry.sub} />
                    <ListCell primary={entry.kindLabel} mobile />
                    <ListCell primary={formatCalendarTime(entry.date, preferences)} />
                    <ListCell primary={entry.detail} />
                    <ListCell>
                      {entry.unaired ? (
                        <span className="text-(length:--type-label) text-muted-foreground">
                          {`Airs ${formatRangeDate(entry.date, preferences)}`}
                        </span>
                      ) : (
                        <TitleMarkLabel item={entry.mark} type={entry.mediaType} />
                      )}
                    </ListCell>
                  </ListRow>
                ))}
              </div>
            ))}
          </ListTable>
        )}
      </ListCard>
      )}

      <CalendarSubscribeDrawer open={subscribeOpen} onOpenChange={setSubscribeOpen} />

      <Drawer
        open={optionsOpen}
        onOpenChange={setOptionsOpen}
        title="Calendar options"
        description="What shows on the calendar, and how it is highlighted"
        footer={<DrawerFooter readOnly state="clean" saveLabel="Close" onCancel={() => setOptionsOpen(false)} />}
      >
        <DrawerSection title="Highlighting">
          <SwitchRow
            label="Highlight titles below cutoff"
            description="Rings an entry that has a file but has not yet reached the quality you asked for."
            checked={options.highlightCutoffUnmet}
            onCheckedChange={(checked) => setOptions((current) => ({ ...current, highlightCutoffUnmet: checked }))}
          />
        </DrawerSection>
        <DrawerSection title="Movie release types">
          <SwitchRow
            label="In cinemas"
            checked={options.showInCinemas}
            onCheckedChange={(checked) => setOptions((current) => ({ ...current, showInCinemas: checked }))}
          />
          <SwitchRow
            label="Digital"
            checked={options.showDigital}
            onCheckedChange={(checked) => setOptions((current) => ({ ...current, showDigital: checked }))}
          />
          <SwitchRow
            label="Physical"
            checked={options.showPhysical}
            onCheckedChange={(checked) => setOptions((current) => ({ ...current, showPhysical: checked }))}
          />
        </DrawerSection>
      </Drawer>
    </div>
  );
}

/* -------------------------------------------------------------- helpers */

interface GridCell {
  key: string;
  date: Date;
  inRange: boolean;
  isToday: boolean;
  entries: CalendarEntry[];
}

/**
 * Whole Monday-to-Sunday weeks covering the range. A month rarely starts on a
 * Monday, so the grid spills into the neighbouring months and dims those days
 * rather than leaving holes.
 */
function buildWeeks(
  range: { start: Date; end: Date },
  byDay: Array<[string, CalendarEntry[]]>,
  firstDay: CalendarFirstDay = "monday"
): GridCell[][] {
  const lookup = new Map(byDay);
  const today = new Date();
  today.setHours(0, 0, 0, 0);

  const first = new Date(range.start);
  first.setDate(first.getDate() - dayIndex(first, firstDay));

  const last = new Date(range.end);
  last.setDate(last.getDate() - 1);
  last.setDate(last.getDate() + (6 - dayIndex(last, firstDay)));

  const weeks: GridCell[][] = [];
  const cursor = new Date(first);
  while (cursor <= last) {
    const week: GridCell[] = [];
    for (let day = 0; day < 7; day += 1) {
      const key = isoDate(cursor);
      week.push({
        key,
        date: new Date(cursor),
        inRange: cursor >= range.start && cursor < range.end,
        isToday: cursor.getTime() === today.getTime(),
        entries: lookup.get(key) ?? []
      });
      cursor.setDate(cursor.getDate() + 1);
    }
    weeks.push(week);
  }

  return weeks;
}

function buildEntries(data: CalendarLoaderData): CalendarEntry[] {
  const episodes = data.episodes.map<CalendarEntry>((episode) => {
    return {
      id: `episode:${episode.episodeId}`,
      date: new Date(episode.airDateUtc),
      name: episode.seriesTitle,
      sub: `S${String(episode.seasonNumber).padStart(2, "0")}E${String(episode.episodeNumber).padStart(2, "0")}`,
      kindLabel: "Episode",
      mediaType: "show",
      detail: episode.title ?? "Episode title pending",
      mark: { monitored: episode.monitored, wantedStatus: episode.wantedStatus },
      unaired: new Date(episode.airDateUtc).getTime() > Date.now() && (episode.wantedStatus ?? "").toLowerCase() !== "upgrade",
      href: `/tv/${episode.seriesId}`
    };
  });

  const movies = data.movies.map<CalendarEntry>((movie) => ({
    id: `movie:${movie.movieId}:${movie.kind}:${movie.date}`,
    date: new Date(`${movie.date}T00:00:00`),
    name: movie.title,
    sub: movie.releaseYear ? String(movie.releaseYear) : "Movie",
    kindLabel: movieKindLabel(movie.kind),
    mediaType: "movie",
    detail: movieKindDetail(movie.kind),
    mark: { monitored: movie.monitored, wantedStatus: movie.wantedStatus },
    href: `/movies/${movie.movieId}`,
    movieKind: movie.kind
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

function buildRange(scope: Scope, offset: number, preferences: DisplayPreferences) {
  const anchor = new Date();
  anchor.setHours(0, 0, 0, 0);

  if (scope === "day") {
    const start = new Date(anchor);
    start.setDate(anchor.getDate() + offset);
    const end = new Date(start);
    end.setDate(start.getDate() + 1);
    return { start, end, label: formatDayLabel(start, preferences) };
  }

  if (scope === "week") {
    const weekday = dayIndex(anchor, preferences.calendarFirstDayOfWeek);
    const start = new Date(anchor);
    start.setDate(anchor.getDate() - weekday + offset * 7);
    const end = new Date(start);
    end.setDate(start.getDate() + 7);
    return { start, end, label: formatWeekLabel(start, end, preferences) };
  }

  if (scope === "agenda") {
    const start = new Date(anchor);
    start.setDate(anchor.getDate() + offset * AGENDA_WINDOW_DAYS);
    const end = new Date(start);
    end.setDate(start.getDate() + AGENDA_WINDOW_DAYS);
    return { start, end, label: formatWeekLabel(start, end, preferences) };
  }

  const start = new Date(anchor.getFullYear(), anchor.getMonth() + offset, 1);
  const end = new Date(start.getFullYear(), start.getMonth() + 1, 1);
  return {
    start,
    end,
    label: formatMonth(start, preferences)
  };
}

function formatWeekLabel(start: Date, end: Date, preferences: DisplayPreferences) {
  const last = new Date(end);
  last.setDate(end.getDate() - 1);
  const startPart = formatRangeDate(start, preferences);
  const endPart = formatRangeDate(last, preferences);
  return `${startPart} – ${endPart}`;
}

function formatDayLabel(date: Date, preferences: DisplayPreferences) {
  return formatLongDate(date, preferences);
}

function formatCalendarTime(date: Date, preferences: DisplayPreferences) {
  // Most air dates carry no real time and are stored at midnight UTC as a
  // placeholder; a source with the network's actual broadcast time (#860)
  // gives one worth showing, converted into the user's chosen timezone.
  return hasBroadcastTime(date) ? formatPreferenceTime(date, preferences) : "All day";
}

function dayIndex(date: Date, firstDay: CalendarFirstDay) {
  return firstDay === "sunday" ? date.getDay() : (date.getDay() + 6) % 7;
}

function isoDate(date: Date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

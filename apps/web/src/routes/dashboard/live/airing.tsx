/**
 * Airing soon (#1096): the next episodes to air, each with a poster and a
 * countdown to its air time, and one line that says whether Deluno will search
 * for them on its own. The episodes are the monitored ones from the same record
 * the Schedule screen reads; the line comes from the TV libraries' automatic
 * search, which is off until a person turns it on.
 */
import { Link } from "react-router-dom";
import { ArtworkImage } from "../../../components/ui/artwork-image";
import type { SeriesUpcomingEpisodeItem } from "../../../lib/api";
import type { DashboardSummary } from "../../../lib/api/types/operations";
import { cn } from "../../../lib/utils";
import { FitList, Panel, Plain } from "../parts";
import { initialsOf } from "./card";
import { useNow } from "./hooks";

const episodeCode = (item: SeriesUpcomingEpisodeItem) => `S${String(item.seasonNumber).padStart(2, "0")}E${String(item.episodeNumber).padStart(2, "0")}`;

/** "in 24 min", "in 3 h 20 min", "tomorrow 9:00 pm", "Fri 9:00 pm": how long until it airs, by the minute. */
export function airsIn(value: string, now: number): { text: string; soon: boolean } {
  const then = Date.parse(value);
  if (!Number.isFinite(then)) return { text: "soon", soon: false };
  const minutes = Math.round((then - now) / 60_000);
  if (minutes <= 0) return { text: "airing now", soon: true };
  if (minutes < 60) return { text: `in ${minutes} min`, soon: true };
  if (minutes < 24 * 60) return { text: `in ${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, "0")} min`, soon: minutes < 6 * 60 };
  const when = new Date(then);
  const days = Math.round((new Date(when.getFullYear(), when.getMonth(), when.getDate()).getTime() - new Date(new Date(now).getFullYear(), new Date(now).getMonth(), new Date(now).getDate()).getTime()) / 86_400_000);
  const clock = when.toLocaleTimeString("en-AU", { hour: "numeric", minute: "2-digit" });
  return { text: days === 1 ? `tomorrow ${clock}` : `${when.toLocaleDateString("en-AU", { weekday: "short" })} ${clock}`, soon: false };
}

/** Whether a TV library searches on its own: it has a search scheduled. Said once, for the whole list. */
export function searchesTvAutomatically(summary: DashboardSummary | undefined): boolean | null {
  const libraries = summary?.automation?.libraries;
  if (!libraries) return null;
  return libraries.some((library) => library.mediaType.toLowerCase() === "tv" && Boolean(library.nextSearchUtc || library.nextMissingSearchUtc || library.nextUpgradeSearchUtc));
}

export function AiringSoon({ upcoming, summary, compact = false }: { upcoming: SeriesUpcomingEpisodeItem[]; summary: DashboardSummary | undefined; /** The column is too short for a list: the next episode alone, and how many more. */ compact?: boolean }) {
  const now = useNow(30_000);
  const automatic = searchesTvAutomatically(summary);
  if (compact) {
    return (
      <Panel title="Airing soon" count={upcoming.length > 0 ? `${upcoming.length.toLocaleString("en-AU")} in the next 7 days` : "next 7 days"} to="/calendar" toLabel="Schedule">
        {upcoming.length === 0 ? <Plain>Nothing you follow airs in the next 7 days.</Plain> : <ul><Row item={upcoming[0]} now={now} /></ul>}
      </Panel>
    );
  }
  return (
    <Panel title="Airing soon" count="next 7 days" to="/calendar" toLabel="Schedule">
      {automatic === null ? null : (
        <p className="shrink-0 border-b border-hairline px-[var(--card-pad-x)] py-1 text-[length:var(--type-caption)] text-muted-foreground">
          {automatic ? "Deluno searches for these on its own." : "Automatic search for TV is off, so these are not searched for."}
        </p>
      )}
      {upcoming.length === 0 ? (
        <Plain>Nothing you follow airs in the next 7 days.</Plain>
      ) : (
        <FitList
          wrap={(shown) => <ul>{shown}</ul>}
          moreTo="/calendar"
          rows={upcoming.map((item) => <Row key={item.episodeId} item={item} now={now} />)}
        />
      )}
    </Panel>
  );
}

function Row({ item, now, ...rest }: { item: SeriesUpcomingEpisodeItem; now: number; "data-fit-row"?: string }) {
  const air = airsIn(item.airDateUtc, now);
  return (
    <li {...rest} className="border-b border-hairline last:border-b-0">
      <Link
        to={`/tv/${item.seriesId}`}
        aria-label={`${item.title} ${episodeCode(item)}: ${air.text}`}
        className="flex items-center gap-2.5 px-[var(--card-pad-x)] py-1.5 transition-colors hover:bg-primary/[0.05] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        <span aria-hidden className="block h-[2.625rem] w-[1.75rem] shrink-0 overflow-hidden rounded">
          <ArtworkImage
            src={item.posterUrl}
            alt=""
            loading="lazy"
            className="h-full w-full bg-surface-3 object-cover"
            fallback={<span className="flex h-full w-full items-center justify-center bg-surface-3 text-[length:var(--type-micro)] font-semibold text-muted-foreground">{initialsOf(item.title)}</span>}
          />
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[length:var(--type-body-sm)] font-medium leading-tight text-foreground">{item.title}</span>
          <span className="block truncate text-[length:var(--type-caption)] leading-tight text-muted-foreground">{episodeCode(item)}{item.episodeTitle ? ` · ${item.episodeTitle}` : ""}</span>
        </span>
        <span className={cn("shrink-0 text-right text-[length:var(--type-caption)] font-semibold tabular-nums", air.soon ? "text-primary" : "text-muted-foreground")}>{air.text}</span>
      </Link>
    </li>
  );
}

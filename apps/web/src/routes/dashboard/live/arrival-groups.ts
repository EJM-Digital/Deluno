/**
 * Arrived today (#1098): one poster per film, and one poster per show per day however many
 * of its episodes landed (twelve Breaking Bad episodes are one poster, not twelve).
 */
import type { ArrivalItem } from "./extras";
import { nameOf } from "./arrivals";
import { episodeFromRelease } from "./journey";
import { isGone, type TitleLookup } from "./titles";

export interface ArrivalGroup {
  key: string;
  /** Every arrival in the group, newest first. */
  items: ArrivalItem[];
  title: string;
  poster?: string;
  to: string;
  /** The newest import in the group. */
  at: string;
  /** The quality tag: one quality, "mixed" when they differ, an arrow for a single upgrade. */
  tag: string | null;
  /** The quality for the poster's bar: the one quality, "mixed" when they differ, the new one for a single upgrade. */
  quality: string | null;
  /** The quality a single upgrade replaced. */
  previousQuality: string | null;
  /** How many in the group were upgrades. */
  upgrades: number;
  /** "S01E04" for one episode, "12 episodes" for several, empty for a film. */
  episodes: string | null;
  /** For several episodes of one season whose numbers are known: "S01E05–E08 · 4 episodes"; null otherwise. */
  episodeSpan: string | null;
  sizeBytes: number;
  /** Which kind of title it is: a show (TV) or a film (Movies). */
  library: ArrivalLibrary;
  /** The library it landed in, for its tag and the shelf's chips (#1103); null when not known. */
  libraryId: string | null;
  libraryName: string | null;
  /** The Movies page's state for what arrived: true quality met (gold), false can still be upgraded (green), null not known. A group is met only when every member is. */
  met: boolean | null;
}

export type ArrivalLibrary = "tv" | "movies";

const dayOf = (at: string) => {
  const date = new Date(at);
  return Number.isNaN(date.getTime()) ? at : `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
};

const code = (season: number, episode: number) => `S${String(season).padStart(2, "0")}E${String(episode).padStart(2, "0")}`;

/** The show an arrival belongs to, when it is an episode (or a series import) of one the library knows. */
function seriesOf(item: ArrivalItem, titles: TitleLookup): string | null {
  const entity = item.entity;
  if (!entity) return null;
  if (entity.kind === "series") return entity.id;
  if (entity.kind === "episode") return titles.episodes.get(entity.id)?.seriesId ?? null;
  return null;
}

function episodeCode(item: ArrivalItem, titles: TitleLookup): string | null {
  const entity = item.entity;
  if (entity?.kind === "episode") {
    const info = titles.episodes.get(entity.id);
    if (info) return code(info.season, info.episode);
  }
  return item.release ? episodeFromRelease(item.release) : null;
}

/** "S01E05–E08 · 4 episodes" for a run of one season's episodes, or null where the numbers are not all known or they span seasons. */
export function episodeSpanOf(codes: ReadonlyArray<string | null>): string | null {
  if (codes.length < 2) return null;
  const parsed = codes.map((value) => /^S(\d+)E(\d+)$/i.exec(value ?? ""));
  if (parsed.some((match) => !match)) return null;
  const seasons = new Set(parsed.map((match) => Number(match![1])));
  if (seasons.size !== 1) return null;
  const numbers = parsed.map((match) => Number(match![2]));
  const first = Math.min(...numbers);
  const last = Math.max(...numbers);
  const two = (value: number) => String(value).padStart(2, "0");
  return `S${two([...seasons][0])}E${two(first)}${last === first ? "" : `–E${two(last)}`} · ${codes.length} episodes`;
}

/** Quality met only when every arrival in the group met it; upgradable when any can still be upgraded; unknown when none says. */
export function groupMet(members: readonly ArrivalItem[]): boolean | null {
  if (members.some((item) => item.qualityMet === false)) return false;
  if (members.length > 0 && members.every((item) => item.qualityMet === true)) return true;
  return null;
}

export function groupArrivals(items: ArrivalItem[], titles: TitleLookup): ArrivalGroup[] {
  const order: string[] = [];
  const buckets = new Map<string, ArrivalItem[]>();
  for (const item of items) {
    // A title that is no longer in the library is not on the shelf.
    if (isGone(item.entity, titles)) continue;
    const series = seriesOf(item, titles);
    const key = series ? `show:${series}:${dayOf(item.at)}:${item.libraryId ?? ""}` : `one:${item.key}`;
    const bucket = buckets.get(key);
    if (bucket) bucket.push(item);
    else {
      buckets.set(key, [item]);
      order.push(key);
    }
  }
  const groups = order.map((key): ArrivalGroup => {
    const members = [...(buckets.get(key) ?? [])].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
    const first = members[0];
    const named = nameOf(first, titles);
    const isShow = key.startsWith("show:");
    const upgrades = members.filter((item) => item.upgraded).length;
    const qualities = new Set(members.map((item) => item.quality).filter((quality): quality is string => Boolean(quality)));
    let tag: string | null;
    if (members.length === 1) tag = first.upgraded && first.previousQuality && first.quality ? `${first.previousQuality} → ${first.quality}` : first.quality;
    else tag = qualities.size > 1 ? "mixed" : (qualities.values().next().value ?? null);
    if (members.length > 1 && upgrades > 0) tag = `${tag ?? "Arrived"} · ${upgrades} upgraded`;
    const showName = isShow ? named.title.replace(/\s+S\d{1,2}E\d{1,3}.*$/i, "") : named.title;
    return {
      key: members.length === 1 ? first.key : `group:${key}`,
      items: members,
      title: showName,
      poster: named.poster,
      to: named.to,
      at: first.at,
      tag,
      quality: members.length === 1 ? first.quality : qualities.size > 1 ? "mixed" : (qualities.values().next().value ?? null),
      previousQuality: members.length === 1 ? first.previousQuality : null,
      upgrades,
      episodes: !isShow ? null : members.length === 1 ? episodeCode(first, titles) : `${members.length} episodes`,
      episodeSpan: isShow && members.length > 1 ? episodeSpanOf(members.map((item) => episodeCode(item, titles))) : null,
      sizeBytes: members.reduce((sum, item) => sum + (item.sizeBytes ?? 0), 0),
      libraryId: first.libraryId ?? null,
      libraryName: first.libraryName ?? null,
      library: isShow || first.entity?.kind === "series" || first.entity?.kind === "episode" ? "tv" : "movies",
      met: groupMet(members)
    };
  });
  return groups.sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
}

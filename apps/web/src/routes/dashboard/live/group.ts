/**
 * Consecutive events that say the same thing become one line (#1096): six
 * searches for the same show, one after another, read "Searched Lioness
 * S02E03–E08: nothing better than what you have", with how many they were.
 * Only neighbours merge, so the order of the day is never rearranged, and a
 * group says each event's time in its tooltip.
 */
import { activityKind } from "./kinds";
import type { LiveActivityRow } from "../live-events";
import { entityFromRelated, namedTitle, type TitleLookup } from "./titles";

/** What an episode search found, in a few words. */
export function outcomeWords(outcome: string): string {
  const said = outcome.trim().toLowerCase();
  if (said === "skipped") return "nothing better than what you have";
  if (/^(queued|grabbed|sent)/.test(said)) return "found a release and sent it to your client";
  if (/no[-\s_]?(results?|match|release)/.test(said)) return "no release found";
  return said.replace(/[-_]+/g, " ");
}

const SEARCH = /^Episode search executed:\s*S(\d{1,2})E(\d{1,3})\s*-\s*(.+)$/i;

const code = (season: number, episode: number) => `S${String(season).padStart(2, "0")}E${String(episode).padStart(2, "0")}`;

/** The sentence for one event: a search for an episode names the show, and says what it found, instead of repeating the server's bookkeeping. */
export function sentenceOf(item: Pick<LiveActivityRow, "message" | "relatedEntityType" | "relatedEntityId">, titles: TitleLookup): string {
  const search = SEARCH.exec(item.message);
  if (!search) return item.message;
  const named = namedTitle(entityFromRelated(item.relatedEntityType, item.relatedEntityId), titles);
  const episode = code(Number(search[1]), Number(search[2]));
  return `Searched ${named ? `${named.name} ${episode}` : episode} — ${outcomeWords(search[3])}`;
}

export interface ActivityGroup {
  /** The newest row, which names the group, links it and says when. */
  first: LiveActivityRow;
  rows: LiveActivityRow[];
  sentence: string;
  /** One line per event: what it said and when, for the tooltip. */
  detail: string[];
  fresh: boolean;
}

interface SearchPart {
  show: string;
  season: number;
  episode: number;
  outcome: string;
}

function searchPart(row: LiveActivityRow, titles: TitleLookup): SearchPart | null {
  const match = SEARCH.exec(row.message);
  if (!match) return null;
  const named = namedTitle(entityFromRelated(row.relatedEntityType, row.relatedEntityId), titles);
  return { show: named?.name ?? "", season: Number(match[1]), episode: Number(match[2]), outcome: outcomeWords(match[3]) };
}

function searchSentence(parts: SearchPart[]): string {
  const ordered = [...parts].sort((a, b) => a.season - b.season || a.episode - b.episode);
  const low = ordered[0];
  const high = ordered[ordered.length - 1];
  const distinct = new Set(ordered.map((part) => code(part.season, part.episode)));
  let range = code(low.season, low.episode);
  if (distinct.size > 1) range += low.season === high.season ? `–E${String(high.episode).padStart(2, "0")}` : `–${code(high.season, high.episode)}`;
  return `Searched ${low.show ? `${low.show} ` : ""}${range} — ${low.outcome}`;
}

/**
 * Groups neighbouring events. Episode searches merge when they are for the same
 * show (or both for an unnamed one) and found the same thing; any other events
 * merge when they say exactly the same sentence in the same kind.
 */
export function groupActivity(rows: LiveActivityRow[], titles: TitleLookup, when: (row: LiveActivityRow) => string): ActivityGroup[] {
  const groups: ActivityGroup[] = [];
  let parts: SearchPart[] = [];
  const close = (group: ActivityGroup) => {
    group.detail = group.rows.map((row) => `${sentenceOf(row, titles)} (${when(row)})`);
    group.fresh = group.rows.some((row) => row.fresh);
  };
  for (const row of rows) {
    const part = searchPart(row, titles);
    const last = groups[groups.length - 1];
    const lastPart = parts[0];
    if (last) {
      const sameSearch = part && lastPart && part.show === lastPart.show && part.outcome === lastPart.outcome;
      const sameSentence = !part && !lastPart && last.first.category === row.category && activityKind(row.category, row.severity) === activityKind(last.first.category, last.first.severity) && sentenceOf(last.first, titles) === sentenceOf(row, titles);
      if (sameSearch || sameSentence) {
        last.rows.push(row);
        if (part) {
          parts.push(part);
          last.sentence = searchSentence(parts);
        }
        continue;
      }
    }
    if (last) close(last);
    parts = part ? [part] : [];
    groups.push({ first: row, rows: [row], sentence: part ? searchSentence([part]) : sentenceOf(row, titles), detail: [], fresh: false });
  }
  const last = groups[groups.length - 1];
  if (last) close(last);
  return groups;
}

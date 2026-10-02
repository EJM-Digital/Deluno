/**
 * The words under a shelf poster (James, 2 Oct 2026): nothing is drawn on the art but the picture and the library's tag, so what
 * the quality bar and the upgrade arrow used to say is the status line below it. The caption is a block of fixed line slots, so
 * every poster's lines are level across the row: the title, the status (a dot in the Movies page's own legend colour and words),
 * a quiet detail line and when. A line with nothing to say keeps its space.
 */
import { formatSize } from "../downloads-kit";
import type { ArrivalGroup } from "./arrival-groups";

/** The status dot's colour is the Movies page's legend: gold for quality met, green for can still be upgraded, grey when not known. */
export type StatusTone = "met" | "upgradable" | "unknown";

export interface PosterStatus {
  tone: StatusTone;
  /** "Quality met · Bluray 1080p", "Can be upgraded · WEB 720p", "Upgraded · Bluray 1080p", or "Imported" when nothing is known. */
  words: string;
}

type StatusFields = Pick<ArrivalGroup, "met" | "quality" | "upgrades" | "items">;
type DetailFields = Pick<ArrivalGroup, "episodes" | "episodeSpan" | "previousQuality" | "upgrades" | "items" | "sizeBytes">;

/** The status line: an upgrade that landed keeps its met or upgradable colour and says "Upgraded". */
export function posterStatus(group: StatusFields): PosterStatus {
  const tone: StatusTone = group.met === true ? "met" : group.met === false ? "upgradable" : "unknown";
  let lead: string | null = null;
  if (group.upgrades > 0) lead = group.items.length > 1 ? `${group.upgrades} upgraded` : "Upgraded";
  else if (tone === "met") lead = "Quality met";
  else if (tone === "upgradable") lead = "Can be upgraded";
  if (lead === null) return { tone, words: "Imported" };
  return { tone, words: group.quality ? `${lead} · ${group.quality}` : lead };
}

/** The detail line: the episodes ("S01E05–E08 · 4 episodes"), what an upgrade replaced ("from WEB 720p"), or else the size ("2.5 GB"). */
export function posterDetail(group: DetailFields): string {
  const parts: string[] = [];
  const episodes = group.episodeSpan ?? group.episodes;
  if (episodes) parts.push(episodes);
  if (group.upgrades > 0 && group.items.length === 1 && group.previousQuality) parts.push(`from ${group.previousQuality}`);
  else if (!episodes && group.sizeBytes > 0) parts.push(formatSize(group.sizeBytes).replace(/\.0 /, " "));
  return parts.join(" · ");
}

import type { MediaSubtitleRow } from "./api";

/**
 * Plain, hearing-impaired or forced (#923) — the one fact a title's own page
 * (movie or episode) asks about a subtitle it already has. Shared so the two
 * detail pages never grow their own copy of this rule.
 */
export function subtitleTypeLabel(subtitle: MediaSubtitleRow): string {
  if (subtitle.forced) return "Forced";
  if (subtitle.hearingImpaired) return "Hearing-impaired";
  return "Plain";
}

export function subtitleSourceLabel(source: string): string {
  switch (source) {
    case "fetched":
      return "Fetched";
    case "embedded":
      return "Inside the video";
    default:
      return "Found beside the video";
  }
}

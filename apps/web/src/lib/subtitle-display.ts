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

/**
 * #922: what the last timing sync found, in plain words — "Timing: not
 * checked" rather than the stored code, so a person reads a sentence rather
 * than a status string.
 */
export function timingStatusLabel(subtitle: MediaSubtitleRow): string {
  switch (subtitle.timingStatus) {
    case "moved":
      return "Timing: adjusted";
    case "already_in_time":
      return "Timing: in time";
    case "not_checked":
      return "Timing: not checked";
    default:
      return "Timing: never synced";
  }
}

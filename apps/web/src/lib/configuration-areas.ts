/**
 * The seven configuration areas, and which paths belong to each.
 *
 * The matcher decides which tabs the sidebar and toolbar show for a path. (Each area used to open with an
 * explainer panel; the owner removed them all in favour of short lines and an (i) icon, #1430.)
 */

export interface ConfigurationArea {
  id: string;
  match: (path: string) => boolean;
}

const MEDIA_MANAGEMENT_PATHS = [
  "/settings/libraries",
  "/settings/media-management",
  "/settings/import-policy",
  "/settings/processing",
  "/settings/destination-rules",
  "/settings/metadata",
  "/settings/subtitles",
  "/settings/tags",
  "/settings/auto-tagging"
];

const PREFERENCES_PATHS = ["/settings/general", "/settings/ui", "/settings/notifications", "/settings/migration"];

export const CONFIGURATION_AREAS: readonly ConfigurationArea[] = [
  {
    id: "media-management",
    match: (path) => path === "/settings" || MEDIA_MANAGEMENT_PATHS.some((item) => path.startsWith(item))
  },
  {
    id: "quality-and-release",
    match: (path) =>
      path.startsWith("/settings/policy-sets") ||
      path.startsWith("/settings/profiles") ||
      path.startsWith("/settings/quality") ||
      path.startsWith("/settings/custom-formats") ||
      path.startsWith("/settings/release-rules")
  },
  {
    id: "find-and-download",
    match: (path) => path.startsWith("/indexers")
  },
  {
    id: "automation-and-recovery",
    match: (path) => path.startsWith("/search-cycles")
  },
  {
    id: "discover-media",
    match: (path) => path.startsWith("/settings/lists")
  },
  {
    id: "preferences",
    match: (path) => PREFERENCES_PATHS.some((item) => path.startsWith(item))
  },
  {
    id: "system",
    // Deliberately not `/setup-guide`, which is its own screen and lights no sidebar item.
    match: (path) => path.startsWith("/system")
  }
];

export function findConfigurationArea(pathname: string) {
  return CONFIGURATION_AREAS.find((area) => area.match(pathname));
}

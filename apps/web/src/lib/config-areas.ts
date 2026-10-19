/**
 * The Settings areas a `ConfigChanged` event can name (#1179). Mirrors ConfigAreas in
 * src/Deluno.Contracts/SystemChangeEvents.cs.
 */
export const ConfigAreas = {
  tags: "tags",
  autoTagging: "auto-tagging",
  customFormats: "custom-formats",
  qualityModel: "quality-model",
  notifications: "notifications",
  retention: "retention",
  mediaServers: "media-servers",
  subtitles: "subtitles",
  security: "security",
  releaseRules: "release-rules",
  destinationRules: "destination-rules",
  downloadHealth: "download-health",
  processors: "processors",
  lists: "lists"
} as const;

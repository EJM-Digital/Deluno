export type IndexerProtocol = "torznab" | "newznab" | "rss" | "custom";
export type MediaScope = "movies" | "tv" | "both";

export const INDEXER_PRESETS: { protocol: IndexerProtocol; label: string; hint: string; requiresApiKey: boolean; placeholder: string; defaultCategories: (scope: MediaScope) => string }[] = [
  { protocol: "torznab", label: "Torznab", hint: "Any Torznab-compatible tracker or private site.", requiresApiKey: true, placeholder: "http://localhost:9117/api/v2.0/indexers/XXX/results/torznab/", defaultCategories: (scope) => (scope === "movies" ? "2000,2010,2020,2030,2040,2045,2050,2060,2070" : scope === "tv" ? "5000,5010,5020,5030,5040,5045,5050,5060,5070" : "2000,2010,2020,2030,2040,2045,2050,2060,5000,5010,5020,5030,5040,5045,5050") },
  { protocol: "newznab", label: "Newznab", hint: "NZBGeek, DrunkenSlug, NZBCat, or any Newznab-compatible Usenet indexer.", requiresApiKey: true, placeholder: "https://api.nzbgeek.info", defaultCategories: (scope) => (scope === "movies" ? "2000,2010,2020,2030,2040,2045,2050,2060,2070" : scope === "tv" ? "5000,5010,5020,5030,5040,5045,5050,5060,5070" : "2000,2010,2020,2030,2040,2045,2050,2060,5000,5010,5020,5030,5040,5045,5050") },
  { protocol: "rss", label: "RSS feed", hint: "Plain RSS feed without authentication.", requiresApiKey: false, placeholder: "https://example.com/feed.rss", defaultCategories: () => "" },
  { protocol: "custom", label: "Custom", hint: "Manual configuration for anything else.", requiresApiKey: false, placeholder: "https://example.com", defaultCategories: () => "" }
];

export const CLIENT_PRESETS = [
  { protocol: "qbittorrent", label: "qBittorrent", kind: "Torrent", defaultPort: 8080, defaultMoviesCategory: "deluno-movies", defaultTvCategory: "deluno-tv", authMode: "Web UI login", setupHint: "Enable the Web UI and use the same username and password you use in qBittorrent." },
  { protocol: "transmission", label: "Transmission", kind: "Torrent", defaultPort: 9091, defaultMoviesCategory: "deluno-movies", defaultTvCategory: "deluno-tv", authMode: "Basic auth", setupHint: "Use the RPC port. Deluno handles the Transmission session token automatically." },
  { protocol: "deluge", label: "Deluge", kind: "Torrent", defaultPort: 8112, defaultMoviesCategory: "deluno-movies", defaultTvCategory: "deluno-tv", authMode: "Password", setupHint: "Use the Deluge Web UI password. Labels are used as Deluno categories." },
  { protocol: "utorrent", label: "uTorrent", kind: "Torrent", defaultPort: 8080, defaultMoviesCategory: "deluno-movies", defaultTvCategory: "deluno-tv", authMode: "Token auth", setupHint: "Use the Web UI credentials. uTorrent may not expose a reliable finished path, so imports can need a file-location link." },
  { protocol: "sabnzbd", label: "SABnzbd", kind: "Usenet", defaultPort: 8080, defaultMoviesCategory: "Movies", defaultTvCategory: "TV", authMode: "API key", setupHint: "Paste the SABnzbd API key into the password field. Categories map directly to SABnzbd folders." },
  { protocol: "nzbget", label: "NZBGet", kind: "Usenet", defaultPort: 6789, defaultMoviesCategory: "Movies", defaultTvCategory: "TV", authMode: "Basic auth", setupHint: "Use the NZBGet username and password." }
] as const;

export const PRIORITY_OPTIONS = [{ label: "Highest (1)", value: "1" }, { label: "High (5)", value: "5" }, { label: "Normal (10)", value: "10" }, { label: "Low (25)", value: "25" }, { label: "Fallback only (50)", value: "50" }];
export const HOST_OPTIONS = [{ label: "This machine (localhost)", value: "localhost" }, { label: "Localhost IPv4 (127.0.0.1)", value: "127.0.0.1" }, { label: "Docker host (host.docker.internal)", value: "host.docker.internal" }];
export const CATEGORY_OPTIONS = [{ label: "deluno-movies", value: "deluno-movies" }, { label: "Movies", value: "Movies" }, { label: "movies", value: "movies" }, { label: "radarr", value: "radarr" }];
export const TV_CATEGORY_OPTIONS = [{ label: "deluno-tv", value: "deluno-tv" }, { label: "TV", value: "TV" }, { label: "tv", value: "tv" }, { label: "sonarr", value: "sonarr" }];
export const PORT_OPTIONS = [...new Map(CLIENT_PRESETS.map((item) => [String(item.defaultPort), item] as const)).entries()].map(([value, item]) => ({ value, label: `${CLIENT_PRESETS.filter((candidate) => candidate.defaultPort === item.defaultPort).map((candidate) => candidate.label).join(" / ")} default (${value})` }));

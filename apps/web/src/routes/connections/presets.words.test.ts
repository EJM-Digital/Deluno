import { describe, expect, it } from "vitest";
import { CATEGORY_OPTIONS, CLIENT_PRESETS, HOST_OPTIONS, INDEXER_PRESETS, KNOWN_INDEXER_FLAGS, PORT_OPTIONS, PRIORITY_OPTIONS, TV_CATEGORY_OPTIONS } from "./presets";

describe("the words a person reads when choosing an indexer type", () => {
  it("says what each type is, and shows an example address", () => {
    expect(INDEXER_PRESETS.map(({ defaultCategories: _categories, ...rest }) => rest)).toEqual([
      { protocol: "tracker", label: "Tracker (built in)", hint: "A torrent site Deluno searches itself. No Prowlarr or Jackett needed.", requiresApiKey: false, placeholder: "https://tracker.example/" },
      { protocol: "torznab", label: "Torznab", hint: "Any Torznab-compatible tracker or private site.", requiresApiKey: true, placeholder: "http://localhost:9117/api/v2.0/indexers/XXX/results/torznab/" },
      { protocol: "newznab", label: "Newznab", hint: "NZBGeek, DrunkenSlug, NZBCat, or any Newznab-compatible Usenet indexer.", requiresApiKey: true, placeholder: "https://api.nzbgeek.info" },
      { protocol: "rss", label: "RSS feed", hint: "Plain RSS feed without authentication.", requiresApiKey: false, placeholder: "https://example.com/feed.rss" },
      { protocol: "custom", label: "Custom", hint: "Manual configuration for anything else.", requiresApiKey: false, placeholder: "https://example.com" }
    ]);
  });

  it("offers the flags that sites report most often", () => {
    expect(KNOWN_INDEXER_FLAGS).toEqual(["Freeleech", "Half Leech", "Double Upload", "Internal", "Reward Points"]);
  });
});

describe("the words a person reads when choosing a download client", () => {
  it("says how each client is reached and what to set up in it", () => {
    expect(CLIENT_PRESETS.map((client) => [client.protocol, client.label, client.kind, client.authMode, client.defaultUrlBase])).toEqual([
      ["qbittorrent", "qBittorrent", "Torrent", "Web UI login", ""],
      ["transmission", "Transmission", "Torrent", "Basic auth", ""],
      ["deluge", "Deluge", "Torrent", "Password", ""],
      ["utorrent", "uTorrent", "Torrent", "Token auth", ""],
      ["rtorrent", "rTorrent", "Torrent", "Basic auth", "/RPC2"],
      ["sabnzbd", "SABnzbd", "Usenet", "API key", ""],
      ["nzbget", "NZBGet", "Usenet", "Basic auth", ""]
    ]);
    expect(CLIENT_PRESETS.map((client) => [client.defaultMoviesCategory, client.defaultTvCategory])).toEqual([
      ["deluno-movies", "deluno-tv"], ["deluno-movies", "deluno-tv"], ["deluno-movies", "deluno-tv"], ["deluno-movies", "deluno-tv"], ["deluno-movies", "deluno-tv"],
      ["Movies", "TV"], ["Movies", "TV"]
    ]);
    expect(CLIENT_PRESETS.every((client) => client.setupHint.length > 20)).toBe(true);
    expect(CLIENT_PRESETS[0].setupHint).toContain("Enable the Web UI");
    expect(CLIENT_PRESETS[1].setupHint).toContain("RPC port");
    expect(CLIENT_PRESETS[2].setupHint).toContain("Deluge Web UI password");
    expect(CLIENT_PRESETS[3].setupHint).toContain("Web UI credentials");
    expect(CLIENT_PRESETS[4].setupHint).toContain("XML-RPC");
    expect(CLIENT_PRESETS[5].setupHint).toContain("API key");
    expect(CLIENT_PRESETS[6].setupHint).toContain("NZBGet username and password");
  });
});

describe("the choices offered beside the fields", () => {
  it("offers the usual priorities from highest to fallback", () => {
    expect(PRIORITY_OPTIONS).toEqual([
      { label: "Highest (1)", value: "1" }, { label: "High (5)", value: "5" }, { label: "Normal (10)", value: "10" }, { label: "Low (25)", value: "25" }, { label: "Fallback only (50)", value: "50" }
    ]);
  });

  it("offers the usual hosts", () => {
    expect(HOST_OPTIONS).toEqual([
      { label: "This machine (localhost)", value: "localhost" }, { label: "Localhost IPv4 (127.0.0.1)", value: "127.0.0.1" }, { label: "Docker host (host.docker.internal)", value: "host.docker.internal" }
    ]);
  });

  it("offers the usual categories for films and for shows", () => {
    expect(CATEGORY_OPTIONS).toEqual([
      { label: "deluno-movies", value: "deluno-movies" }, { label: "Movies", value: "Movies" }, { label: "movies", value: "movies" }, { label: "radarr", value: "radarr" }
    ]);
    expect(TV_CATEGORY_OPTIONS).toEqual([
      { label: "deluno-tv", value: "deluno-tv" }, { label: "TV", value: "TV" }, { label: "tv", value: "tv" }, { label: "sonarr", value: "sonarr" }
    ]);
  });

  it("offers each port once, naming the clients that use it", () => {
    expect(PORT_OPTIONS).toEqual([
      { value: "8080", label: "qBittorrent / uTorrent / SABnzbd default (8080)" },
      { value: "9091", label: "Transmission default (9091)" },
      { value: "8112", label: "Deluge default (8112)" },
      { value: "8000", label: "rTorrent default (8000)" },
      { value: "6789", label: "NZBGet default (6789)" }
    ]);
  });
});

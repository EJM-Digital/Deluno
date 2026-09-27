import { describe, expect, it } from "vitest";
import { CLIENT_PRESETS } from "./presets";
import type { IndexerItem } from "../../lib/api";
import type { DownloadClientItem } from "../../lib/api";
import { clientFormErrors, clientFormFrom, emptyClientForm, emptyIndexerForm, indexerFormErrors, indexerFormFrom, sameIndexer } from "./forms";

/**
 * #293 — a create form opens with values the user is entitled to accept as-is.
 * "Unchanged from the defaults" is not "nothing to save", so the defaults have
 * to satisfy the very rule that gates Save.
 */
describe("create form defaults", () => {
  it("a new download client is valid the moment the drawer opens", () => {
    expect(clientFormErrors(emptyClientForm())).toEqual({});
  });

  it("names the new client after the preset rather than leaving it blank", () => {
    expect(emptyClientForm().name).toBe(CLIENT_PRESETS[0]!.label);
  });

  it("a new download client defaults Use SSL off and URL base blank", () => {
    expect(emptyClientForm().useSsl).toBe(false);
    expect(emptyClientForm().urlBase).toBe("");
  });

  it("a new download client defaults Add downloads paused off", () => {
    expect(emptyClientForm().addPaused).toBe(false);
  });
});

// #804: Use SSL and URL Base round-trip through the client form the same way
// every other saved field does.
describe("client form Use SSL and URL Base", () => {
  const baseItem: DownloadClientItem = {
    id: "client-1",
    name: "SABnzbd",
    protocol: "sabnzbd",
    host: "localhost",
    port: 8080,
    username: null,
    endpointUrl: "https://localhost:8080/sabnzbd/",
    useSsl: true,
    urlBase: "sabnzbd",
    addPaused: false,
    moviesCategory: "movies",
    tvCategory: "tv",
    categoryTemplate: null,
    priority: 1,
    isEnabled: true,
    healthStatus: "healthy",
    lastHealthMessage: null,
    createdUtc: "2026-01-01T00:00:00Z",
    updatedUtc: "2026-01-01T00:00:00Z"
  };

  it("reads a saved client's Use SSL and URL base into the form", () => {
    const form = clientFormFrom(baseItem);
    expect(form.useSsl).toBe(true);
    expect(form.urlBase).toBe("sabnzbd");
  });

  it("reads a client with Use SSL off and no URL base as blank, not null", () => {
    const form = clientFormFrom({ ...baseItem, useSsl: false, urlBase: null });
    expect(form.useSsl).toBe(false);
    expect(form.urlBase).toBe("");
  });

  it("reads a saved client's Add downloads paused setting into the form", () => {
    expect(clientFormFrom({ ...baseItem, addPaused: true }).addPaused).toBe(true);
    expect(clientFormFrom({ ...baseItem, addPaused: false }).addPaused).toBe(false);
  });

  it("a new indexer says what is missing instead of nothing", () => {
    const errors = indexerFormErrors(emptyIndexerForm(), true);
    expect(Object.keys(errors).sort()).toEqual(["apiKey", "baseUrl", "name"]);
  });

  it("stops asking for the API key once the indexer exists", () => {
    expect(indexerFormErrors({ ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" }, false)).toEqual({});
  });

  // #607: a double-escaped \d refused every number typed into these.
  it("accepts whole numbers for the indexer's limits and refuses anything else", () => {
    const base = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    expect(indexerFormErrors({ ...base, minimumAgeMinutes: "30", retentionDays: "3500", maximumSizeMb: "60000", availabilityDelayDays: "0" }, false)).toEqual({});
    expect(Object.keys(indexerFormErrors({ ...base, minimumAgeMinutes: "3.5", retentionDays: "-1", maximumSizeMb: "big" }, false)).sort())
      .toEqual(["maximumSizeMb", "minimumAgeMinutes", "retentionDays"]);
  });
});

// #606: a built-in tracker is chosen from Deluno's list, not typed as a URL.
describe("built-in trackers", () => {
  const tracker = { ...emptyIndexerForm(), protocol: "tracker" as const, name: "YTS", baseUrl: "https://yts.gg/" };

  it("needs to know which tracker it is, and no API key", () => {
    expect(indexerFormErrors(tracker, true)).toEqual({ definitionId: "Choose which tracker this is." });
    expect(indexerFormErrors({ ...tracker, definitionId: "yts" }, true)).toEqual({});
  });

  it("reads a saved tracker back with its settings and no secrets", () => {
    const form = indexerFormFrom({ ...savedIndexer(), protocol: "tracker", definitionId: "torrentleech", definitionSettings: { sort: "seeders" }, definitionSecretsSet: ["password"] });
    expect(form.protocol).toBe("tracker");
    expect(form.definitionId).toBe("torrentleech");
    expect(form.trackerSettings).toEqual({ sort: "seeders" });
    expect(form.trackerSecrets).toEqual({});
  });

  it("counts a typed secret or a changed setting as a change, and an untouched blank as none", () => {
    const saved = { ...tracker, definitionId: "torrentleech", trackerSettings: { sort: "seeders" } };
    expect(sameIndexer(saved, { ...saved, trackerSettings: { sort: "seeders" }, trackerSecrets: { password: "" } })).toBe(true);
    expect(sameIndexer(saved, { ...saved, trackerSecrets: { password: "hunter2" } })).toBe(false);
    expect(sameIndexer(saved, { ...saved, trackerSettings: { sort: "time" } })).toBe(false);
  });
});

// #740: a non-default API path round-trips through the form the same way
// every other saved field does.
describe("indexer form API path", () => {
  it("reads a saved indexer's custom API path into the form", () => {
    const form = indexerFormFrom({ ...savedIndexer(), apiPath: "/api/v2" });
    expect(form.apiPath).toBe("/api/v2");
  });

  it("reads an indexer with no custom path as blank, not null", () => {
    const form = indexerFormFrom({ ...savedIndexer(), apiPath: null });
    expect(form.apiPath).toBe("");
  });

  it("defaults a new indexer's API path to blank", () => {
    expect(emptyIndexerForm().apiPath).toBe("");
  });
});

// #741: an indexer's extra query parameters round-trip through the form.
describe("indexer form additional parameters", () => {
  it("reads a saved indexer's additional parameters into the form", () => {
    const form = indexerFormFrom({ ...savedIndexer(), additionalParameters: "token=abc123" });
    expect(form.additionalParameters).toBe("token=abc123");
  });

  it("defaults a new indexer's additional parameters to blank", () => {
    expect(emptyIndexerForm().additionalParameters).toBe("");
  });
});

// #742: an indexer's own anime category numbers round-trip through the form.
describe("indexer form anime categories", () => {
  it("reads a saved indexer's anime categories into the form", () => {
    const form = indexerFormFrom({ ...savedIndexer(), animeCategories: "5070" });
    expect(form.animeCategories).toBe("5070");
  });

  it("defaults a new indexer's anime categories to blank", () => {
    expect(emptyIndexerForm().animeCategories).toBe("");
  });
});

function savedIndexer(): IndexerItem {
  return {
    id: "1", name: "TorrentLeech", protocol: "torznab", privacy: "private", baseUrl: "https://www.torrentleech.org/", priority: 10, categories: "2000", tags: "",
    isEnabled: true, healthStatus: "healthy", lastHealthMessage: null, consecutiveFailures: 0, rateLimitedUntilUtc: null, disabledReason: null,
    createdUtc: "2026-09-24T00:00:00Z", updatedUtc: "2026-09-24T00:00:00Z"
  };
}

describe("client validation", () => {
  /**
   * #292 — a client saved by an older Deluno can carry a protocol nothing can
   * dispatch to. The drawer now lets the reader change it, so the form has to
   * refuse to save it unchanged.
   */
  it("refuses a protocol Deluno cannot send to", () => {
    expect(clientFormErrors({ ...emptyClientForm(), protocol: "torrent" }).protocol).toBeTruthy();
  });

  it("accepts every protocol the client picker offers", () => {
    for (const preset of CLIENT_PRESETS) {
      expect(clientFormErrors({ ...emptyClientForm(), protocol: preset.protocol })).toEqual({});
    }
  });

  it("rejects a port that is not a number", () => {
    expect(clientFormErrors({ ...emptyClientForm(), port: "eight" }).port).toBeTruthy();
  });

  it("rejects a blank host", () => {
    expect(clientFormErrors({ ...emptyClientForm(), host: "  " }).host).toBeTruthy();
  });
});

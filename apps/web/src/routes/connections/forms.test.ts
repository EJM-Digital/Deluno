import { describe, expect, it } from "vitest";
import { CLIENT_PRESETS } from "./presets";
import type { IndexerItem } from "../../lib/api";
import type { DownloadClientItem } from "../../lib/api";
import type { ProxyItem } from "../../lib/api";
import { clientFormErrors, clientFormFrom, emptyClientForm, emptyIndexerForm, emptyProxyForm, indexerFormErrors, indexerFormFrom, proxyFormErrors, proxyFormFrom, sameIndexer, sameProxy } from "./forms";

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

  // #806 (#936): nothing automatic is on by default, including for a client
  // added after the feature shipped.
  it("a new download client defaults Remove completed and Remove failed off", () => {
    expect(emptyClientForm().removeCompleted).toBe(false);
    expect(emptyClientForm().removeFailed).toBe(false);
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

  // #807 — the drawer never rendered Priority, so create/edit always sent a
  // hard-coded fallback instead of what the person chose. Reopening the
  // drawer has to show the value that was actually saved.
  it("reads a saved client's priority into the form on reopen", () => {
    expect(clientFormFrom({ ...baseItem, priority: 37 }).priority).toBe("37");
  });

  // #806 — a single platform-wide RemoveCompletedDownloads flag is now two
  // per-client toggles, both off unless the person turns them on for this
  // client, and both have to read back on reopen.
  it("reads a saved client's Remove completed/failed toggles into the form on reopen, defaulting off", () => {
    expect(clientFormFrom(baseItem).removeCompleted).toBe(false);
    expect(clientFormFrom(baseItem).removeFailed).toBe(false);
    expect(clientFormFrom({ ...baseItem, removeCompleted: true }).removeCompleted).toBe(true);
    expect(clientFormFrom({ ...baseItem, removeFailed: true }).removeFailed).toBe(true);
  });

  // #811 — qBittorrent's add-time toggles and share limits, all off/blank
  // unless set, and reading back whatever was actually saved.
  it("reads a saved client's qBittorrent add-time toggles and share limits into the form on reopen", () => {
    const empty = clientFormFrom(baseItem);
    expect(empty.skipHashCheck).toBe(false);
    expect(empty.sequentialDownload).toBe(false);
    expect(empty.firstLastPiecePrio).toBe(false);
    expect(empty.shareRatioLimit).toBe("");
    expect(empty.shareSeedingTimeLimitMinutes).toBe("");
    expect(empty.shareInactiveSeedingTimeLimitMinutes).toBe("");

    const set = clientFormFrom({
      ...baseItem,
      skipHashCheck: true,
      sequentialDownload: true,
      firstLastPiecePrio: true,
      shareRatioLimit: 2.5,
      shareSeedingTimeLimitMinutes: 4320,
      shareInactiveSeedingTimeLimitMinutes: 60
    });
    expect(set.skipHashCheck).toBe(true);
    expect(set.sequentialDownload).toBe(true);
    expect(set.firstLastPiecePrio).toBe(true);
    expect(set.shareRatioLimit).toBe("2.5");
    expect(set.shareSeedingTimeLimitMinutes).toBe("4320");
    expect(set.shareInactiveSeedingTimeLimitMinutes).toBe("60");
  });

  // #999 — content layout starts on the client's own setting and reads back what was saved.
  it("reads a saved client's content layout into the form on reopen", () => {
    expect(emptyClientForm().contentLayout).toBe("default");
    expect(clientFormFrom(baseItem).contentLayout).toBe("default");
    expect(clientFormFrom({ ...baseItem, contentLayout: "no-subfolder" }).contentLayout).toBe("no-subfolder");
  });

  // #811 — qBittorrent's save path (setLocation/savepath), blank unless set.
  it("reads a saved client's save path into the form on reopen", () => {
    expect(clientFormFrom(baseItem).location).toBe("");
    expect(clientFormFrom({ ...baseItem, location: "D:\\Downloads\\Movies" }).location).toBe("D:\\Downloads\\Movies");
  });

  it("rejects a negative or non-numeric share limit but accepts a blank one", () => {
    const base = { ...emptyClientForm(), name: "qBittorrent", host: "localhost" };
    expect(clientFormErrors({ ...base, shareRatioLimit: "" })).toEqual({});
    expect(clientFormErrors({ ...base, shareRatioLimit: "2.5" })).toEqual({});
    expect(Object.keys(clientFormErrors({ ...base, shareRatioLimit: "-1" }))).toContain("shareRatioLimit");
    expect(Object.keys(clientFormErrors({ ...base, shareSeedingTimeLimitMinutes: "12.5" }))).toContain("shareSeedingTimeLimitMinutes");
    expect(Object.keys(clientFormErrors({ ...base, shareInactiveSeedingTimeLimitMinutes: "-5" }))).toContain("shareInactiveSeedingTimeLimitMinutes");
  });

  // #808 — no Tags field existed at all; reopening a saved client has to show
  // whatever tags it carries, and an untagged client reads back blank.
  it("reads a saved client's tags into the form on reopen", () => {
    expect(clientFormFrom({ ...baseItem, tags: "anime,4k" }).tags).toBe("anime,4k");
    expect(clientFormFrom({ ...baseItem, tags: undefined }).tags).toBe("");
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

  // #738: a minimum-seeders floor is validated the same way as the other
  // acquisition limits, and stays unrestricted when left blank.
  it("accepts a whole-number minimum-seeders floor and refuses a negative one", () => {
    const base = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    expect(indexerFormErrors({ ...base, minimumSeeders: "5" }, false)).toEqual({});
    expect(indexerFormErrors({ ...base, minimumSeeders: "" }, false)).toEqual({});
    expect(Object.keys(indexerFormErrors({ ...base, minimumSeeders: "-1" }, false)))
      .toEqual(["minimumSeeders"]);
  });

  // #744: required/ignored word lists reject by keyword, separate from the
  // ranking-only preferIndexerFlags field.
  it("refuses a required or ignored word list over 500 characters", () => {
    const base = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const long = "a".repeat(501);
    expect(indexerFormErrors({ ...base, requiredWords: "remux" }, false)).toEqual({});
    expect(Object.keys(indexerFormErrors({ ...base, requiredWords: long }, false))).toEqual(["requiredWords"]);
    expect(Object.keys(indexerFormErrors({ ...base, ignoredWords: long }, false))).toEqual(["ignoredWords"]);
  });

  // #746: a per-indexer MultiLanguages setting, validated like the other
  // free-text acquisition fields.
  it("refuses a multi-languages value over 200 characters", () => {
    const base = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    expect(indexerFormErrors({ ...base, multiLanguages: "Original, French" }, false)).toEqual({});
    expect(Object.keys(indexerFormErrors({ ...base, multiLanguages: "a".repeat(201) }, false))).toEqual(["multiLanguages"]);
  });

  // #750: query/grab limits validated like the other numeric acquisition fields.
  it("accepts whole-number query/grab limits and refuses negative ones", () => {
    const base = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    expect(indexerFormErrors({ ...base, queryLimit: "100", grabLimit: "20" }, false)).toEqual({});
    expect(Object.keys(indexerFormErrors({ ...base, queryLimit: "-1", grabLimit: "-2" }, false)).sort())
      .toEqual(["grabLimit", "queryLimit"]);
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

  // #807 — Priority matches the existing contract range: 1 (highest) to 50 (lowest).
  it("accepts a priority within 1-50 and rejects anything outside it", () => {
    expect(clientFormErrors({ ...emptyClientForm(), priority: "1" })).toEqual({});
    expect(clientFormErrors({ ...emptyClientForm(), priority: "50" })).toEqual({});
    expect(clientFormErrors({ ...emptyClientForm(), priority: "0" }).priority).toBeTruthy();
    expect(clientFormErrors({ ...emptyClientForm(), priority: "51" }).priority).toBeTruthy();
    expect(clientFormErrors({ ...emptyClientForm(), priority: "abc" }).priority).toBeTruthy();
  });
});

// #748 — Indexer Proxies: a proxy assignable to an indexer, in place of the
// single global Cloudflare-helper address.
describe("proxy form", () => {
  function proxyItem(overrides: Partial<ProxyItem> = {}): ProxyItem {
    return {
      id: "proxy-1",
      name: "Home SOCKS5",
      proxyType: "socks5",
      address: "socks5://192.168.1.1:1080",
      username: null,
      hasPassword: false,
      isEnabled: true,
      createdUtc: "2026-09-24T00:00:00Z",
      updatedUtc: "2026-09-24T00:00:00Z",
      assignedIndexerNames: [],
      ...overrides
    };
  }

  it("a new proxy is not valid until it has a name and an address", () => {
    expect(proxyFormErrors(emptyProxyForm())).not.toEqual({});
    expect(proxyFormErrors({ ...emptyProxyForm(), name: "Proxy", address: "http://a.test:8080" })).toEqual({});
  });

  it("round-trips a saved proxy into the form, with password always blank", () => {
    const form = proxyFormFrom(proxyItem({ username: "user", hasPassword: true }));
    expect(form).toEqual({ name: "Home SOCKS5", proxyType: "socks5", address: "socks5://192.168.1.1:1080", username: "user", password: "", isEnabled: true });
  });

  it("sameProxy ignores nothing but the form's own fields", () => {
    const a = proxyFormFrom(proxyItem());
    const b = proxyFormFrom(proxyItem());
    expect(sameProxy(a, b)).toBe(true);
    expect(sameProxy(a, { ...b, name: "Renamed" })).toBe(false);
  });
});

// #748 — an indexer's own proxy assignment round-trips through the form.
describe("indexer form proxy assignment", () => {
  it("defaults to no proxy (direct connection)", () => {
    expect(emptyIndexerForm().proxyId).toBe("");
  });

  it("round-trips an assigned proxy id from a saved indexer", () => {
    const form = indexerFormFrom({ ...savedIndexer(), proxyId: "proxy-1" });
    expect(form.proxyId).toBe("proxy-1");
  });

  it("reads a direct connection back as blank, not null", () => {
    const form = indexerFormFrom({ ...savedIndexer(), proxyId: null });
    expect(form.proxyId).toBe("");
  });
});

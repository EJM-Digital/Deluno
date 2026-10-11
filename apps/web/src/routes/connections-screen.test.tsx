import { describe, expect, it } from "vitest";
import { buildClientSavePayload, buildIndexerSavePayload } from "./connections-screen";
import { clientFormFrom, clientsForPathMapping, emptyClientForm, emptyIndexerForm } from "./connections/forms";
import type { DownloadClientItem, DownloadClientPathMappingItem } from "../lib/api";

/**
 * #990 — tags now decide which titles an indexer is searched for, so the
 * field the drawer edits (form.tags) has to be what the save payload sends,
 * for both a new indexer and an edit of one that already carries tags.
 */
describe("indexer save payload (#990)", () => {
  it("sends the typed tags", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", tags: "anime, 4k" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.tags).toBe("anime, 4k");
  });

  it("sends no tags for an untagged indexer", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, false);
    expect(payload.tags).toBe("");
  });
});

// #740: a custom API path is sent when set, and clearing it tells the backend
// to drop back to the default rather than leaving the saved value untouched.
describe("indexer save payload API path (#740)", () => {
  it("sends a typed API path", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", apiPath: "/api/v2" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.apiPath).toBe("/api/v2");
    expect(payload.clearApiPath).toBe(false);
  });

  it("clears the API path on an edit when the field is emptied", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", apiPath: "" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.apiPath).toBeNull();
    expect(payload.clearApiPath).toBe(true);
  });

  it("does not ask to clear the API path when creating a new indexer", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, false);
    expect(payload.clearApiPath).toBe(false);
  });
});

// #738: a per-indexer minimum-seeders floor is sent when typed and cleared
// when the field is emptied on an edit, the same clear-flag pattern as the
// other acquisition limits.
describe("indexer save payload minimum seeders (#738)", () => {
  it("sends a typed minimum-seeders floor", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", minimumSeeders: "5" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.minimumSeeders).toBe(5);
    expect(payload.clearMinimumSeeders).toBe(false);
  });

  it("clears the minimum-seeders floor on an edit when the field is emptied", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", minimumSeeders: "" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.minimumSeeders).toBeNull();
    expect(payload.clearMinimumSeeders).toBe(true);
  });

  it("does not ask to clear minimum seeders when creating a new indexer", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, false);
    expect(payload.clearMinimumSeeders).toBe(false);
  });
});

// #744: required/ignored word lists round-trip the same clear-flag way as
// the other acquisition limits.
describe("indexer save payload required/ignored words (#744)", () => {
  it("sends typed required and ignored words", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", requiredWords: "remux", ignoredWords: "cam, korsub" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.requiredWords).toBe("remux");
    expect(payload.ignoredWords).toBe("cam, korsub");
    expect(payload.clearRequiredWords).toBe(false);
    expect(payload.clearIgnoredWords).toBe(false);
  });

  it("clears required and ignored words on an edit when emptied", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.requiredWords).toBeNull();
    expect(payload.ignoredWords).toBeNull();
    expect(payload.clearRequiredWords).toBe(true);
    expect(payload.clearIgnoredWords).toBe(true);
  });
});

// #746: what a per-indexer MULTI tag means round-trips the same clear-flag
// way as the other acquisition fields.
describe("indexer save payload multi languages (#746)", () => {
  it("sends a typed multi-languages meaning", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", multiLanguages: "Original, French" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.multiLanguages).toBe("Original, French");
    expect(payload.clearMultiLanguages).toBe(false);
  });

  it("clears the multi-languages meaning on an edit when emptied", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.multiLanguages).toBeNull();
    expect(payload.clearMultiLanguages).toBe(true);
  });
});

// #747: a recorded VIP/subscription expiry date round-trips as an ISO
// timestamp and clears the same clear-flag way as the other acquisition
// fields.
describe("indexer save payload VIP expiry (#747)", () => {
  it("sends a typed VIP expiry date as an ISO timestamp", () => {
    const form = { ...emptyIndexerForm(), name: "PrivateHD", baseUrl: "https://tracker.example.test", vipExpiresUtc: "2026-12-01" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.vipExpiresUtc).toBe(new Date("2026-12-01T00:00:00.000Z").toISOString());
    expect(payload.clearVipExpiry).toBe(false);
  });

  it("clears the VIP expiry date on an edit when emptied", () => {
    const form = { ...emptyIndexerForm(), name: "PrivateHD", baseUrl: "https://tracker.example.test" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.vipExpiresUtc).toBeNull();
    expect(payload.clearVipExpiry).toBe(true);
  });
});

// #750: query/grab limits and their unit round-trip the same clear-flag way
// as the other acquisition fields.
describe("indexer save payload query/grab limits (#750)", () => {
  it("sends typed query/grab limits and the chosen unit", () => {
    const form = { ...emptyIndexerForm(), name: "PrivateHD", baseUrl: "https://tracker.example.test", queryLimit: "100", grabLimit: "20", limitsUnit: "hour" as const };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.queryLimit).toBe(100);
    expect(payload.grabLimit).toBe(20);
    expect(payload.limitsUnit).toBe("hour");
    expect(payload.clearQueryLimit).toBe(false);
    expect(payload.clearGrabLimit).toBe(false);
  });

  it("clears query/grab limits on an edit when emptied", () => {
    const form = { ...emptyIndexerForm(), name: "PrivateHD", baseUrl: "https://tracker.example.test" };
    const payload = buildIndexerSavePayload(form, form, true);
    expect(payload.queryLimit).toBeNull();
    expect(payload.grabLimit).toBeNull();
    expect(payload.clearQueryLimit).toBe(true);
    expect(payload.clearGrabLimit).toBe(true);
  });
});

// #752: prefer-magnet-links sends through as a plain boolean, no clear flag
// needed since it is never "unset", only on or off.
describe("indexer save payload prefer magnet (#752)", () => {
  it("sends the chosen prefer-magnet-links value", () => {
    const form = { ...emptyIndexerForm(), name: "PrivateTracker", baseUrl: "https://tracker.example.test", protocol: "torznab" as const, preferMagnetUrl: true };
    expect(buildIndexerSavePayload(form, form, true).preferMagnetUrl).toBe(true);
  });

  it("defaults to off for a new indexer", () => {
    const form = { ...emptyIndexerForm(), name: "PrivateTracker", baseUrl: "https://tracker.example.test" };
    expect(buildIndexerSavePayload(form, form, false).preferMagnetUrl).toBe(false);
  });
});

// #743: a per-indexer TV search restriction sends through as a plain string,
// with "both" as the always-present default.
describe("indexer save payload season-pack search mode (#743)", () => {
  it("sends the chosen season-pack search mode", () => {
    const form = { ...emptyIndexerForm(), name: "PrivateHD", baseUrl: "https://tracker.example.test", seasonPackSearchMode: "episodes-only" as const };
    expect(buildIndexerSavePayload(form, form, true).seasonPackSearchMode).toBe("episodes-only");
  });

  it("defaults to both for a new indexer", () => {
    const form = { ...emptyIndexerForm(), name: "PrivateHD", baseUrl: "https://tracker.example.test" };
    expect(buildIndexerSavePayload(form, form, false).seasonPackSearchMode).toBe("both");
  });
});

/**
 * #807 — Priority existed in the API contract and server-side ordering but the
 * drawer never rendered it, so create/edit always sent a hard-coded fallback
 * (10 or 1) instead of the value the person chose.
 */
describe("client save payload (#807)", () => {
  it("sends the person's chosen priority instead of the hard-coded fallback", () => {
    const form = { ...emptyClientForm(), name: "qBittorrent", host: "localhost", priority: "37" };
    expect(buildClientSavePayload(form).priority).toBe(37);
  });

  it("falls back to 1 only when the priority field is actually blank", () => {
    const form = { ...emptyClientForm(), name: "qBittorrent", host: "localhost", priority: "" };
    expect(buildClientSavePayload(form).priority).toBe(1);
  });

  // The full loop: a create sends a chosen priority, the server hands the
  // saved item back with that value, an edit of that same item sends the
  // same value again, and reopening the drawer afterwards still shows it.
  it("round-trips a chosen priority through create, edit, and reopen", () => {
    const createForm = { ...emptyClientForm(), name: "qBittorrent", host: "localhost", priority: "37" };
    const createPayload = buildClientSavePayload(createForm);
    expect(createPayload.priority).toBe(37);

    const saved: DownloadClientItem = {
      id: "client-1",
      name: "qBittorrent",
      protocol: "qbittorrent",
      host: "localhost",
      port: 8080,
      username: null,
      endpointUrl: "http://localhost:8080/",
      useSsl: false,
      urlBase: "",
      addPaused: false,
      moviesCategory: "",
      tvCategory: "",
      categoryTemplate: null,
      priority: createPayload.priority,
      isEnabled: true,
      healthStatus: "healthy",
      lastHealthMessage: null,
      createdUtc: "2026-01-01T00:00:00Z",
      updatedUtc: "2026-01-01T00:00:00Z"
    };

    const reopenedForm = clientFormFrom(saved);
    expect(reopenedForm.priority).toBe("37");

    const editPayload = buildClientSavePayload(reopenedForm);
    expect(editPayload.priority).toBe(37);
  });
});

/** Golden path rc.30.9: saving Transmission's priority rewrote its host to http://localhost:9091. */
describe("saving a saved client keeps its address", () => {
  const stored: DownloadClientItem = {
    id: "client-2",
    name: "Transmission",
    protocol: "transmission",
    host: "192.168.1.10",
    port: 9091,
    username: null,
    endpointUrl: "http://192.168.1.10:9091/transmission/rpc",
    useSsl: false,
    urlBase: "",
    addPaused: false,
    moviesCategory: "",
    tvCategory: "",
    categoryTemplate: null,
    priority: 1,
    isEnabled: true,
    healthStatus: "healthy",
    lastHealthMessage: null,
    createdUtc: "2026-01-01T00:00:00Z",
    updatedUtc: "2026-01-01T00:00:00Z"
  };

  it("sends no host, port or URL when only the priority changed", () => {
    const saved = clientFormFrom(stored);
    const payload = buildClientSavePayload({ ...saved, priority: "2" }, saved) as Record<string, unknown>;

    expect(payload.priority).toBe(2);
    for (const key of ["host", "port", "endpointUrl", "useSsl", "urlBase"]) expect(payload).not.toHaveProperty(key);
  });

  it("sends the address when the host was edited", () => {
    const saved = clientFormFrom(stored);
    const payload = buildClientSavePayload({ ...saved, host: "nas.local" }, saved) as Record<string, unknown>;

    expect(payload.host).toBe("nas.local");
    expect(payload.endpointUrl).toBe("http://nas.local:9091");
  });

  it("keeps an address that was never stored as a host", () => {
    const saved = clientFormFrom({ ...stored, host: null as unknown as string });
    const payload = buildClientSavePayload({ ...saved, priority: "3" }, saved) as Record<string, unknown>;

    expect(payload).not.toHaveProperty("host");
  });
});

/**
 * #806 — one platform-wide RemoveCompletedDownloads flag applied identically
 * to every client; it is now two per-client toggles, both off unless the
 * person turns them on for that client.
 */
describe("client save payload completed-download handling (#806)", () => {
  it("sends Remove completed and Remove failed off by default", () => {
    const form = { ...emptyClientForm(), name: "qBittorrent", host: "localhost" };
    expect(buildClientSavePayload(form).removeCompleted).toBe(false);
    expect(buildClientSavePayload(form).removeFailed).toBe(false);
  });

  it("sends each toggle independently when the person turns it on", () => {
    const completedOnly = { ...emptyClientForm(), name: "qBittorrent", host: "localhost", removeCompleted: true };
    expect(buildClientSavePayload(completedOnly).removeCompleted).toBe(true);
    expect(buildClientSavePayload(completedOnly).removeFailed).toBe(false);

    const failedOnly = { ...emptyClientForm(), name: "SABnzbd", host: "localhost", removeFailed: true };
    expect(buildClientSavePayload(failedOnly).removeCompleted).toBe(false);
    expect(buildClientSavePayload(failedOnly).removeFailed).toBe(true);
  });

  it("round-trips two clients with different settings independently", () => {
    const clientA = { ...emptyClientForm(), name: "qBittorrent", host: "localhost", removeCompleted: true, removeFailed: false };
    const clientB = { ...emptyClientForm(), name: "SABnzbd", host: "localhost", removeCompleted: false, removeFailed: true };
    expect(buildClientSavePayload(clientA)).toMatchObject({ removeCompleted: true, removeFailed: false });
    expect(buildClientSavePayload(clientB)).toMatchObject({ removeCompleted: false, removeFailed: true });
  });
});

/**
 * #811 — qBittorrent's add-time toggles and share limits. Blank share-limit
 * fields send null plus a clear flag, so an edit that blanks a previously
 * saved limit actually clears it rather than leaving the old value in place.
 */
describe("client save payload qBittorrent options (#811)", () => {
  it("sends the add-time toggles as booleans", () => {
    const form = { ...emptyClientForm(), name: "qBittorrent", host: "localhost", skipHashCheck: true, sequentialDownload: true, firstLastPiecePrio: true };
    expect(buildClientSavePayload(form)).toMatchObject({ skipHashCheck: true, sequentialDownload: true, firstLastPiecePrio: true });
  });

  it("sends the chosen content layout, and default when none was chosen (#999)", () => {
    const base = { ...emptyClientForm(), name: "qBittorrent", host: "localhost" };
    expect(buildClientSavePayload(base).contentLayout).toBe("default");
    expect(buildClientSavePayload({ ...base, contentLayout: "subfolder" }).contentLayout).toBe("subfolder");
  });

  it("sends null and a clear flag for a blank share limit", () => {
    const form = { ...emptyClientForm(), name: "qBittorrent", host: "localhost" };
    const payload = buildClientSavePayload(form);
    expect(payload.shareRatioLimit).toBeNull();
    expect(payload.clearShareRatioLimit).toBe(true);
    expect(payload.shareSeedingTimeLimitMinutes).toBeNull();
    expect(payload.clearShareSeedingTimeLimitMinutes).toBe(true);
    expect(payload.shareInactiveSeedingTimeLimitMinutes).toBeNull();
    expect(payload.clearShareInactiveSeedingTimeLimitMinutes).toBe(true);
  });

  it("sends a typed share limit as a number, with the clear flag off", () => {
    const form = { ...emptyClientForm(), name: "qBittorrent", host: "localhost", shareRatioLimit: "2.5", shareSeedingTimeLimitMinutes: "4320", shareInactiveSeedingTimeLimitMinutes: "60" };
    const payload = buildClientSavePayload(form);
    expect(payload.shareRatioLimit).toBe(2.5);
    expect(payload.clearShareRatioLimit).toBe(false);
    expect(payload.shareSeedingTimeLimitMinutes).toBe(4320);
    expect(payload.clearShareSeedingTimeLimitMinutes).toBe(false);
    expect(payload.shareInactiveSeedingTimeLimitMinutes).toBe(60);
    expect(payload.clearShareInactiveSeedingTimeLimitMinutes).toBe(false);
  });

  it("sends null and a clear flag for a blank save path, or the typed path otherwise", () => {
    const blank = buildClientSavePayload({ ...emptyClientForm(), name: "qBittorrent", host: "localhost" });
    expect(blank.location).toBeNull();
    expect(blank.clearLocation).toBe(true);

    const set = buildClientSavePayload({ ...emptyClientForm(), name: "qBittorrent", host: "localhost", location: "D:\\Downloads\\Movies" });
    expect(set.location).toBe("D:\\Downloads\\Movies");
    expect(set.clearLocation).toBe(false);
  });
});

/**
 * #808 — Radarr/Sonarr route a tagged title only to a client sharing its tag;
 * an untagged client stays usable for anything. The drawer had no field for
 * this at all, so the payload always sent nothing.
 */
describe("client save payload tags (#808)", () => {
  it("sends the client's tags, trimmed", () => {
    const form = { ...emptyClientForm(), name: "qBittorrent Anime", host: "localhost", tags: " anime, 4k " };
    expect(buildClientSavePayload(form).tags).toBe("anime, 4k");
  });

  it("sends an empty string for an untagged client", () => {
    const form = { ...emptyClientForm(), name: "qBittorrent", host: "localhost", tags: "" };
    expect(buildClientSavePayload(form).tags).toBe("");
  });
});

function client(overrides: Partial<DownloadClientItem>): DownloadClientItem {
  return {
    id: "client-1",
    name: "Client",
    protocol: "qbittorrent",
    host: null,
    port: null,
    username: null,
    endpointUrl: null,
    useSsl: false,
    urlBase: "",
    addPaused: false,
    moviesCategory: "",
    tvCategory: "",
    categoryTemplate: null,
    priority: 1,
    isEnabled: true,
    healthStatus: "healthy",
    lastHealthMessage: null,
    createdUtc: "2026-01-01T00:00:00Z",
    updatedUtc: "2026-01-01T00:00:00Z",
    ...overrides
  };
}

function mapping(overrides: Partial<DownloadClientPathMappingItem>): DownloadClientPathMappingItem {
  return {
    id: "mapping-1",
    downloadClientId: "client-1",
    remotePath: "/downloads/complete",
    localPath: "D:\\Downloads\\complete",
    isEnabled: true,
    priority: 10,
    createdUtc: "2026-01-01T00:00:00Z",
    updatedUtc: "2026-01-01T00:00:00Z",
    host: "nas.local",
    ...overrides
  };
}

/**
 * #809 — the shared Remote Path Mappings screen's "Applies to" column has to
 * show the same set of clients the backend will actually match a release's
 * reported path against (SqliteConnectionsRepository.ListDownloadClientPathMappingsAsync),
 * not just the one client the mapping happens to be filed under.
 */
describe("clientsForPathMapping (#809)", () => {
  it("lists every client sharing the mapping's host, not only the one it was created under", () => {
    const qbit = client({ id: "client-1", name: "qBittorrent", host: "nas.local" });
    const transmission = client({ id: "client-2", name: "Transmission", host: "NAS.LOCAL" });
    const sab = client({ id: "client-3", name: "SABnzbd", host: "usenet-box" });

    const shared = clientsForPathMapping(mapping({ downloadClientId: "client-1", host: "nas.local" }), [qbit, transmission, sab]);

    expect(shared.map((item) => item.name)).toEqual(["qBittorrent", "Transmission"]);
  });

  it("falls back to just the owning client when it has no host recorded", () => {
    const owner = client({ id: "client-1", name: "qBittorrent", host: null });
    const other = client({ id: "client-2", name: "Transmission", host: "nas.local" });

    const shared = clientsForPathMapping(mapping({ downloadClientId: "client-1", host: null }), [owner, other]);

    expect(shared.map((item) => item.name)).toEqual(["qBittorrent"]);
  });

  it("shows nothing when the owning client has since been removed and no host matches", () => {
    const other = client({ id: "client-2", name: "Transmission", host: "different-host" });

    const shared = clientsForPathMapping(mapping({ downloadClientId: "client-1", host: "nas.local" }), [other]);

    expect(shared).toEqual([]);
  });
});

// #1248: a name given is kept, through the screen as through the API; the address names an indexer only when none is given.
describe("indexer save payload name", () => {
  const blank = emptyIndexerForm();

  it("sends the name a person typed", () => {
    expect(buildIndexerSavePayload({ ...blank, name: " My tracker " }, blank, false).name).toBe("My tracker");
  });

  it("sends a blank name when none was given, so the address names it", () => {
    expect(buildIndexerSavePayload(blank, blank, false).name).toBe("");
  });

  it("does not send a keep-name flag: the server keeps any name it is given", () => {
    expect(buildIndexerSavePayload({ ...blank, name: "My tracker" }, blank, false)).not.toHaveProperty("keepName");
  });
});

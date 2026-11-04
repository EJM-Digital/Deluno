import { describe, expect, it } from "vitest";
import { buildClientSavePayload, buildIndexerSavePayload } from "./connections-screen";
import { emptyClientForm, emptyIndexerForm } from "./connections/forms";

// #1329: what Save and Test send for an indexer and a download client, field by field, filled and blank, new and saved.

const filledIndexer = () => ({
  ...emptyIndexerForm(), name: "  Alpha ", protocol: "torznab" as const, baseUrl: " https://alpha.example ", apiKey: " key ", apiPath: " /api ", additionalParameters: " &x=1 ", animeCategories: " 5070 ",
  priority: "3", requestIntervalSeconds: "4", minimumAgeMinutes: "5", retentionDays: "6", maximumSizeMb: "7", minimumSeeders: "8", preferIndexerFlags: " freeleech ",
  requiredWords: " 1080p ", ignoredWords: " cam ", multiLanguages: " en ", vipExpiresUtc: "2026-12-31", queryLimit: "9", grabLimit: "10", limitsUnit: "hour" as const, preferMagnetUrl: true,
  seasonPackSearchMode: "season-packs-only" as const, availabilityDelayDays: "11", rssEnabled: false, automaticSearchEnabled: false, interactiveSearchEnabled: false, proxyId: "p1",
  categories: "2000,5000", tags: " hd ", scope: "tv" as const, isEnabled: false, seasonPackSeedForHours: "12"
});

describe("what an indexer sends", () => {
  it("sends every field as typed, trimmed, with nothing to clear", () => {
    const form = filledIndexer();
    expect(buildIndexerSavePayload(form, form, true)).toEqual({
      name: "Alpha", protocol: "torznab", baseUrl: "https://alpha.example", apiKey: "key", apiPath: "/api", clearApiPath: false,
      additionalParameters: "&x=1", clearAdditionalParameters: false, animeCategories: "5070", clearAnimeCategories: false,
      priority: 3, requestIntervalSeconds: 4, clearRequestInterval: false, minimumAgeMinutes: 5, clearMinimumAge: false, retentionDays: 6, clearRetention: false,
      maximumSizeMb: 7, clearMaximumSize: false, minimumSeeders: 8, clearMinimumSeeders: false, preferIndexerFlags: "freeleech", clearPreferIndexerFlags: false,
      requiredWords: "1080p", clearRequiredWords: false, ignoredWords: "cam", clearIgnoredWords: false, multiLanguages: "en", clearMultiLanguages: false,
      vipExpiresUtc: "2026-12-31T00:00:00.000Z", clearVipExpiry: false, queryLimit: 9, clearQueryLimit: false, grabLimit: 10, clearGrabLimit: false, limitsUnit: "hour" as const,
      preferMagnetUrl: true, seasonPackSearchMode: "season-packs-only" as const, availabilityDelayDays: 11, clearAvailabilityDelay: false, rssEnabled: false, automaticSearchEnabled: false,
      interactiveSearchEnabled: false, proxyId: "p1", clearProxy: false, categories: "2000,5000", tags: "hd", mediaScope: "tv", isEnabled: false,
      seasonPackSeedForHours: 12, clearSeasonPackSeedForHours: false
    });
  });

  it("sends nothing for blank fields, and asks the server to clear them only when a saved indexer is edited", () => {
    const blank = { ...emptyIndexerForm(), name: "Alpha", baseUrl: "https://alpha.example", priority: "" };
    const cleared = buildIndexerSavePayload(blank, blank, true);
    const added = buildIndexerSavePayload(blank, blank, false);
    const nulls = {
      apiKey: undefined, apiPath: null, additionalParameters: null, animeCategories: null, priority: 10, requestIntervalSeconds: null, minimumAgeMinutes: null, retentionDays: null,
      maximumSizeMb: null, minimumSeeders: null, preferIndexerFlags: null, requiredWords: null, ignoredWords: null, multiLanguages: null, vipExpiresUtc: null, queryLimit: null,
      grabLimit: null, availabilityDelayDays: null, proxyId: null, seasonPackSeedForHours: null
    };
    expect(added).toMatchObject(nulls);
    expect(cleared).toMatchObject(nulls);
    const clears = ["clearApiPath", "clearAdditionalParameters", "clearAnimeCategories", "clearRequestInterval", "clearMinimumAge", "clearRetention", "clearMaximumSize", "clearMinimumSeeders",
      "clearPreferIndexerFlags", "clearRequiredWords", "clearIgnoredWords", "clearMultiLanguages", "clearVipExpiry", "clearQueryLimit", "clearGrabLimit", "clearAvailabilityDelay", "clearProxy", "clearSeasonPackSeedForHours"];
    for (const key of clears) {
      expect((cleared as Record<string, unknown>)[key], key).toBe(true);
      expect((added as Record<string, unknown>)[key], key).toBe(false);
    }
  });

  it("a built-in tracker sends which one it is, its settings and only the secrets that were typed", () => {
    const form = { ...emptyIndexerForm(), name: "T", protocol: "tracker" as const, definitionId: "example", trackerSettings: { site: "a" }, trackerSecrets: { passkey: " abc ", cookie: "   ", empty: "" } };
    expect(buildIndexerSavePayload(form, form, false)).toMatchObject({ definitionId: "example", definitionSettings: { site: "a" }, definitionSecrets: { passkey: " abc " } });
    const plain = buildIndexerSavePayload({ ...form, protocol: "torznab" as const }, form, false);
    expect(plain).not.toHaveProperty("definitionId");
    expect(plain).not.toHaveProperty("definitionSecrets");
  });

  it("the sharing rule is sent only when it was changed: strict, custom with its hours and ratio, or back to inherited", () => {
    const base = { ...emptyIndexerForm(), name: "A", baseUrl: "https://a.example" };
    expect(buildIndexerSavePayload(base, base, true)).not.toHaveProperty("sharingMode");
    expect(buildIndexerSavePayload(base, base, true)).not.toHaveProperty("clearSharingPolicy");
    expect(buildIndexerSavePayload({ ...base, sharingRule: "strict" }, base, true)).toMatchObject({
      sharingMode: "share-then-tidy", sharingForHours: 336, sharingUntilRatio: 1, sharingStuckAction: "keep-waiting", sharingStuckAfterDays: 14, clearSharingPolicy: false
    });
    expect(buildIndexerSavePayload({ ...base, sharingRule: "custom", sharingSeedHours: "48", sharingSeedRatio: "2.5" }, base, true)).toMatchObject({
      sharingMode: "share-then-tidy", sharingForHours: 48, sharingUntilRatio: 2.5, sharingStuckAction: "keep-waiting", sharingStuckAfterDays: 14, clearSharingPolicy: false
    });
    expect(buildIndexerSavePayload({ ...base, sharingRule: "custom" }, base, true)).toMatchObject({ sharingForHours: null, sharingUntilRatio: null });
    expect(buildIndexerSavePayload(base, { ...base, sharingRule: "strict" }, true)).toMatchObject({ clearSharingPolicy: true });
    expect(buildIndexerSavePayload(base, { ...base, sharingRule: "strict" }, true)).not.toHaveProperty("sharingMode");
  });
});

describe("what a download client sends", () => {
  const filled = () => ({
    ...emptyClientForm(), name: " Mine ", protocol: "sabnzbd", host: " nas.example ", port: "9090", username: "me", password: "secret", moviesCategory: "films", tvCategory: "shows",
    priority: "4", isEnabled: false, useSsl: true, urlBase: "/sabnzbd/", addPaused: true, tags: " a ", removeCompleted: true, removeFailed: true, skipHashCheck: true,
    sequentialDownload: true, firstLastPiecePrio: true, shareRatioLimit: "2", shareSeedingTimeLimitMinutes: "60", shareInactiveSeedingTimeLimitMinutes: "30", location: " D:\\Dl ", contentLayout: "original"
  });

  it("sends every field as typed, with the address built from the host, port, secure choice and trimmed URL base", () => {
    expect(buildClientSavePayload(filled())).toEqual({
      name: "Mine", protocol: "sabnzbd", host: "nas.example", port: 9090, endpointUrl: "https://nas.example:9090/sabnzbd", useSsl: true, urlBase: "sabnzbd",
      username: "me", password: "secret", moviesCategory: "films", tvCategory: "shows", categoryTemplate: "films", priority: 4, isEnabled: false, addPaused: true, tags: "a",
      removeCompleted: true, removeFailed: true, skipHashCheck: true, sequentialDownload: true, firstLastPiecePrio: true,
      shareRatioLimit: 2, clearShareRatioLimit: false, shareSeedingTimeLimitMinutes: 60, clearShareSeedingTimeLimitMinutes: false,
      shareInactiveSeedingTimeLimitMinutes: 30, clearShareInactiveSeedingTimeLimitMinutes: false, location: "D:\\Dl", clearLocation: false, contentLayout: "original"
    });
  });

  it("a plain address has no URL base, blanks are cleared, and a blank port falls back to the client's own", () => {
    const form = { ...emptyClientForm(), host: "localhost", port: "", priority: "", username: "", password: "", contentLayout: "" };
    expect(buildClientSavePayload(form)).toMatchObject({
      host: "localhost", port: 8080, endpointUrl: "http://localhost:8080", useSsl: false, urlBase: undefined, username: undefined, password: undefined, priority: 1,
      shareRatioLimit: null, clearShareRatioLimit: true, shareSeedingTimeLimitMinutes: null, clearShareSeedingTimeLimitMinutes: true,
      shareInactiveSeedingTimeLimitMinutes: null, clearShareInactiveSeedingTimeLimitMinutes: true, location: null, clearLocation: true, contentLayout: "default"
    });
    expect(buildClientSavePayload({ ...form, protocol: "unknown-kind" })).toMatchObject({ port: 8080 });
  });

  it("a saved client sends its address only when the host, port, secure choice or URL base was edited", () => {
    const saved = filled();
    const keys = (form: ReturnType<typeof filled>) => "host" in buildClientSavePayload(form, saved);
    expect(keys(saved)).toBe(false);
    expect(keys({ ...saved, host: "other.example" })).toBe(true);
    expect(keys({ ...saved, port: "9091" })).toBe(true);
    expect(keys({ ...saved, useSsl: false })).toBe(true);
    expect(keys({ ...saved, urlBase: "other" })).toBe(true);
    expect(keys({ ...saved, host: " nas.example ", port: " 9090 ", urlBase: " /sabnzbd/ " })).toBe(false);
  });
});

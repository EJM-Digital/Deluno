import { describe, expect, it } from "vitest";
import { buildClientSavePayload, buildIndexerSavePayload } from "./connections-screen";
import { emptyClientForm, emptyIndexerForm } from "./connections/forms";

/**
 * #730 — tags: "" was hard-coded into every indexer save, so an indexer that
 * had tags set through the API lost them the next time it was saved in the
 * UI. Nothing in Deluno routes searches or selects indexers by tag today, so
 * there is no tags control to round-trip; the payload just has to leave the
 * saved value alone instead of wiping it.
 */
describe("indexer save payload (#730)", () => {
  it("carries a saved indexer's existing tags through an edit instead of wiping them", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, true, "anime,4k");
    expect(payload.tags).toBe("anime,4k");
  });

  it("does not invent tags for a brand-new indexer", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, false, "");
    expect(payload.tags).toBe("");
  });
});

// #740: a custom API path is sent when set, and clearing it tells the backend
// to drop back to the default rather than leaving the saved value untouched.
describe("indexer save payload API path (#740)", () => {
  it("sends a typed API path", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", apiPath: "/api/v2" };
    const payload = buildIndexerSavePayload(form, form, true, "");
    expect(payload.apiPath).toBe("/api/v2");
    expect(payload.clearApiPath).toBe(false);
  });

  it("clears the API path on an edit when the field is emptied", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", apiPath: "" };
    const payload = buildIndexerSavePayload(form, form, true, "");
    expect(payload.apiPath).toBeNull();
    expect(payload.clearApiPath).toBe(true);
  });

  it("does not ask to clear the API path when creating a new indexer", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, false, "");
    expect(payload.clearApiPath).toBe(false);
  });
});

// #738: a per-indexer minimum-seeders floor is sent when typed and cleared
// when the field is emptied on an edit, the same clear-flag pattern as the
// other acquisition limits.
describe("indexer save payload minimum seeders (#738)", () => {
  it("sends a typed minimum-seeders floor", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", minimumSeeders: "5" };
    const payload = buildIndexerSavePayload(form, form, true, "");
    expect(payload.minimumSeeders).toBe(5);
    expect(payload.clearMinimumSeeders).toBe(false);
  });

  it("clears the minimum-seeders floor on an edit when the field is emptied", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", minimumSeeders: "" };
    const payload = buildIndexerSavePayload(form, form, true, "");
    expect(payload.minimumSeeders).toBeNull();
    expect(payload.clearMinimumSeeders).toBe(true);
  });

  it("does not ask to clear minimum seeders when creating a new indexer", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, false, "");
    expect(payload.clearMinimumSeeders).toBe(false);
  });
});

// #744: required/ignored word lists round-trip the same clear-flag way as
// the other acquisition limits.
describe("indexer save payload required/ignored words (#744)", () => {
  it("sends typed required and ignored words", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info", requiredWords: "remux", ignoredWords: "cam, korsub" };
    const payload = buildIndexerSavePayload(form, form, true, "");
    expect(payload.requiredWords).toBe("remux");
    expect(payload.ignoredWords).toBe("cam, korsub");
    expect(payload.clearRequiredWords).toBe(false);
    expect(payload.clearIgnoredWords).toBe(false);
  });

  it("clears required and ignored words on an edit when emptied", () => {
    const form = { ...emptyIndexerForm(), name: "NZBGeek", baseUrl: "https://api.nzbgeek.info" };
    const payload = buildIndexerSavePayload(form, form, true, "");
    expect(payload.requiredWords).toBeNull();
    expect(payload.ignoredWords).toBeNull();
    expect(payload.clearRequiredWords).toBe(true);
    expect(payload.clearIgnoredWords).toBe(true);
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

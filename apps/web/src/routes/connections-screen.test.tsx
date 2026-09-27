import { describe, expect, it } from "vitest";
import { buildIndexerSavePayload } from "./connections-screen";
import { emptyIndexerForm } from "./connections/forms";

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

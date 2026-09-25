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

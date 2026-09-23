import { describe, expect, it } from "vitest";
import { CLIENT_PRESETS } from "./presets";
import { clientFormErrors, emptyClientForm, emptyIndexerForm, indexerFormErrors } from "./forms";

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

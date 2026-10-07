import { describe, expect, it } from "vitest";
import type { LibraryRoutingSnapshot } from "../../lib/api";
import { clientProblemOf, routingStatus } from "./routing-status";

const snapshot = (category: string | null = null): LibraryRoutingSnapshot => ({
  libraryId: "m",
  libraryName: "Movies",
  sources: [{ id: "s", libraryId: "m", indexerId: "i", indexerName: "Indexer", priority: 1, requiredTags: "", excludedTags: "", createdUtc: "", updatedUtc: "" }],
  downloadClients: [{ id: "c", libraryId: "m", downloadClientId: "d", downloadClientName: "Deluge", priority: 1, category, createdUtc: "", updatedUtc: "" }]
});

/** #1133: Library Routing is not Ready while the folder check says a routed client's category or label is wrong. */
describe("routingStatus", () => {
  it("is Ready when the folder check found nothing wrong", () => {
    expect(routingStatus(snapshot(), { state: "done", problem: null, unknown: false }).label).toBe("Ready");
  });

  it("needs a fix, in the check's own words, when a routed client lacks the category or its Label plugin is off", () => {
    const status = routingStatus(snapshot(), { state: "done", problem: "Deluge: Deluge's Label plugin is off.", unknown: false });
    expect(status).toMatchObject({ label: "Needs a fix", needsFix: true, hint: "Deluge: Deluge's Label plugin is off." });
  });

  it("does not read Ready while the check is still running", () => {
    expect(routingStatus(snapshot(), { state: "checking" }).label).toBe("Checking");
  });

  it("says so when a client did not answer, rather than Ready", () => {
    expect(routingStatus(snapshot(), { state: "done", problem: null, unknown: true }).label).toBe("Not fully checked");
  });

  it("keeps saying no indexers or no client first", () => {
    expect(routingStatus({ ...snapshot(), sources: [] }, { state: "done", problem: "x", unknown: false }).label).toBe("No indexers");
    expect(routingStatus({ ...snapshot(), downloadClients: [] }).label).toBe("No client");
  });
});

describe("clientProblemOf", () => {
  it("names the first client link that does not match", () => {
    const report = {
      clients: [
        { clientId: "1", clientName: "qBittorrent", protocol: "qbittorrent", category: "Movies", links: [{ step: "category", status: "ok", message: "fine" }] },
        { clientId: "2", clientName: "SABnzbd", protocol: "sabnzbd", category: "Movies", links: [{ step: "category", status: "problem", message: "No category named Movies." }] }
      ]
    };
    expect(clientProblemOf(report)).toEqual({ problem: "SABnzbd: No category named Movies.", unknown: false });
  });
});

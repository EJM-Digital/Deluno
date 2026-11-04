import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.fn();
const authedFetch = vi.fn();
vi.mock("./api/client", () => ({ fetchJson: (path: string) => fetchJson(path) }));
vi.mock("./use-auth", () => ({ authedFetch: (path: string, init?: RequestInit) => authedFetch(path, init) }));

import { blockReleaseOnly, checkBlockedMatch, checkQueueMatch, findQueueMatch, liftBlock, stopBlockAndSearchAgain } from "./block-release";
import type { BlockedRelease, DownloadTelemetryOverview } from "./api/types";

/** Blocking a release from wherever it is named (J5, #925): finding it in a client's queue, finding an existing block, and the three calls. */
const overview = (...clients: { clientId: string; clientName: string; queue: { id: string; releaseName?: string | null }[] }[]) => ({ clients }) as unknown as DownloadTelemetryOverview;
const client = (id: string, queue: { id: string; releaseName?: string | null }[]) => ({ clientId: id, clientName: `Client ${id}`, queue });
const block = (over: Partial<BlockedRelease>) => ({ id: "b1", releaseName: "Other", torrentHashOrItemId: null, state: "blocked", ...over }) as unknown as BlockedRelease;

beforeEach(() => {
  fetchJson.mockReset();
  authedFetch.mockReset();
});

describe("finding a release in a client's queue", () => {
  const queue = overview(client("c1", [{ id: " AbC123 ", releaseName: "Some.Film.2020" }]), client("c2", [{ id: "zzz", releaseName: "Another.Film" }]));

  it("matches by hash whatever the case or spacing, whatever the name", () => {
    expect(findQueueMatch(queue, { releaseName: "different name", hash: "  abc123 " })).toEqual({ clientId: "c1", clientName: "Client c1", queueItemId: " AbC123 " });
  });

  it("matches by release name when there is no hash, in any client", () => {
    expect(findQueueMatch(queue, { releaseName: " ANOTHER.film " })).toEqual({ clientId: "c2", clientName: "Client c2", queueItemId: "zzz" });
  });

  it("matches by name when the hash is blank or does not match", () => {
    expect(findQueueMatch(queue, { releaseName: "Some.Film.2020", hash: "   " })?.queueItemId).toBe(" AbC123 ");
    expect(findQueueMatch(queue, { releaseName: "Some.Film.2020", hash: "nope" })?.queueItemId).toBe(" AbC123 ");
  });

  it("does not take a blank hash for an item with a blank id", () => {
    expect(findQueueMatch(overview(client("c1", [{ id: "", releaseName: "x" }])), { releaseName: "y", hash: "" })).toBeNull();
  });

  it("finds nothing when neither matches, or the queue has no names", () => {
    expect(findQueueMatch(queue, { releaseName: "Missing", hash: "q" })).toBeNull();
    expect(findQueueMatch(overview(client("c1", [{ id: "a", releaseName: null }])), { releaseName: "Missing" })).toBeNull();
  });

  it("reads the telemetry for the check, and offers no match when the read fails", async () => {
    fetchJson.mockResolvedValueOnce(queue);
    expect(await checkQueueMatch({ releaseName: "another.film" })).toMatchObject({ clientId: "c2" });
    expect(fetchJson).toHaveBeenCalledWith("/api/download-clients/telemetry");
    fetchJson.mockRejectedValueOnce(new Error("down"));
    expect(await checkQueueMatch({ releaseName: "another.film" })).toBeNull();
  });
});

describe("finding a release that is already blocked", () => {
  it("matches by hash or by name, ignoring case and spacing", async () => {
    const byHash = block({ id: "h", torrentHashOrItemId: " HASH1 " });
    const byName = block({ id: "n", releaseName: " The.Release " });
    fetchJson.mockResolvedValue([block({}), byHash, byName]);
    expect(await checkBlockedMatch({ releaseName: "zzz", hash: "hash1" })).toBe(byHash);
    expect(await checkBlockedMatch({ releaseName: "the.release" })).toBe(byName);
    expect(fetchJson).toHaveBeenCalledWith("/api/blocked-releases");
  });

  it("does not count a proposal, or a blank hash against a blank stored hash", async () => {
    fetchJson.mockResolvedValue([block({ state: "proposed", releaseName: "The.Release", torrentHashOrItemId: "h" }), block({ torrentHashOrItemId: "" })]);
    expect(await checkBlockedMatch({ releaseName: "The.Release", hash: "h" })).toBeNull();
    expect(await checkBlockedMatch({ releaseName: "nothing", hash: "" })).toBeNull();
  });

  it("matches a name given with spaces round it", async () => {
    const byName = block({ releaseName: "the.release" });
    fetchJson.mockResolvedValue([byName]);
    expect(await checkBlockedMatch({ releaseName: "  The.Release  " })).toBe(byName);
  });

  it("offers no lift when the read fails", async () => {
    fetchJson.mockRejectedValueOnce(new Error("down"));
    expect(await checkBlockedMatch({ releaseName: "x" })).toBeNull();
  });
});

describe("the three calls", () => {
  it("lifts a block by its id, escaped", async () => {
    authedFetch.mockResolvedValue({ ok: true });
    await liftBlock("a/b c");
    expect(authedFetch).toHaveBeenCalledWith("/api/blocked-releases/a%2Fb%20c", { method: "DELETE" });
  });

  it("blocks only, sending what it knows and nulls for the rest", async () => {
    const saved = { id: "new" };
    authedFetch.mockResolvedValue({ ok: true, json: async () => saved });
    const result = await blockReleaseOnly({ releaseName: "Rel", indexerName: "Idx", hash: "h", mediaType: "movie", entityId: "e", title: "Film" });
    expect(result.blocked).toBe(saved);
    const [path, init] = authedFetch.mock.calls[0];
    expect(path).toBe("/api/blocked-releases");
    expect(init.method).toBe("POST");
    expect(init.headers).toEqual({ "Content-Type": "application/json" });
    expect(JSON.parse(init.body)).toEqual({ releaseName: "Rel", indexerName: "Idx", torrentHashOrItemId: "h", mediaType: "movie", entityId: "e", title: "Film" });
  });

  it("names the release as the title and nulls what is unknown", async () => {
    authedFetch.mockResolvedValue({ ok: true, json: async () => ({}) });
    await blockReleaseOnly({ releaseName: "Rel" });
    expect(JSON.parse(authedFetch.mock.calls[0][1].body)).toEqual({ releaseName: "Rel", indexerName: null, torrentHashOrItemId: null, mediaType: null, entityId: null, title: "Rel" });
  });

  it("returns no block when the server refuses", async () => {
    const response = { ok: false, json: vi.fn() };
    authedFetch.mockResolvedValue(response);
    expect(await blockReleaseOnly({ releaseName: "Rel" })).toEqual({ response, blocked: null });
    expect(response.json).not.toHaveBeenCalled();
  });

  it("stops the download, blocks it and searches again through the Queue's own remove path", async () => {
    authedFetch.mockResolvedValue({ ok: true });
    await stopBlockAndSearchAgain("c 1", "i/1");
    const [path, init] = authedFetch.mock.calls[0];
    expect(path).toBe("/api/download-clients/c%201/queue/i%2F1/remove");
    expect(init.method).toBe("POST");
    expect(init.headers).toEqual({ "Content-Type": "application/json" });
    expect(JSON.parse(init.body)).toEqual({ reason: "failed" });
  });
});

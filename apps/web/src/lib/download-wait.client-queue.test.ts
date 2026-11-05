import { describe, expect, it } from "vitest";
import type { DownloadQueueItem } from "./api";
import { clientQueueNote, isHeldInClientQueue, ordinal } from "./download-wait";

/** #1493: what a download the client holds in its own queue says, in the words the Dashboard and In progress share. */

function item(id: string, overrides: Partial<DownloadQueueItem> = {}): DownloadQueueItem {
  return {
    id, clientId: "qbit", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies", title: id, releaseName: id, category: "movies", status: "queued",
    progress: 0, speedMbps: 0, etaSeconds: 0, sizeBytes: 1_000_000_000, downloadedBytes: 0, peers: 0, indexerName: "Fixture", errorMessage: null,
    addedUtc: "2026-11-05T04:39:00Z", sourcePath: null, libraryId: null, healthFindings: null, ...overrides
  };
}

describe("a download held in its client's own queue", () => {
  it("is held only when the client says queued", () => {
    expect(isHeldInClientQueue(item("a"))).toBe(true);
    for (const status of ["downloading", "paused", "stalled", "importReady"]) expect(isHeldInClientQueue(item("a", { status }))).toBe(false);
  });

  it("names the client and its place among the downloads waiting there, with the limit as the reason", () => {
    // qBittorrent numbers every torrent, running ones too: the running one holds 1, the three waiting 4 to 6.
    const queue = [item("batman", { queuePosition: 4 }), item("barbie", { queuePosition: 5 }), item("deadpool", { queuePosition: 6 }), item("gladiator", { status: "downloading", queuePosition: 1 })];

    expect(clientQueueNote(queue[0], queue, 3)).toEqual({ label: "Waiting in qBittorrent's queue, 1st of 3", why: "qBittorrent runs 3 downloads at once" });
    expect(clientQueueNote(queue[1], queue, 3)?.label).toBe("Waiting in qBittorrent's queue, 2nd of 3");
    expect(clientQueueNote(queue[2], queue, 3)?.label).toBe("Waiting in qBittorrent's queue, 3rd of 3");
  });

  it("counts only the downloads waiting in the same client", () => {
    const queue = [item("a", { queuePosition: 2 }), item("b", { clientId: "other", clientName: "Deluge", queuePosition: 1 })];

    expect(clientQueueNote(queue[0], queue)?.label).toBe("Waiting in qBittorrent's queue, 1st of 1");
    expect(clientQueueNote(queue[1], queue)?.label).toBe("Waiting in Deluge's queue, 1st of 1");
  });

  it("says only that it waits when the client reports no place, and names no reason without a limit", () => {
    const queue = [item("a", { queuePosition: null }), item("b", { queuePosition: 3 })];

    expect(clientQueueNote(queue[0], queue)).toEqual({ label: "Waiting in qBittorrent's queue", why: null });
    expect(clientQueueNote(queue[0], queue, null)?.why).toBeNull();
    expect(clientQueueNote(queue[0], queue, 0)?.why).toBeNull();
  });

  it("says 1 download, not 1 downloads", () => {
    expect(clientQueueNote(item("a"), [item("a")], 1)?.why).toBe("qBittorrent runs 1 download at once");
  });

  it("is nothing for a download that is not held", () => {
    expect(clientQueueNote(item("a", { status: "downloading" }), [], 3)).toBeNull();
  });

  it("writes places in words", () => {
    expect([1, 2, 3, 4, 11, 12, 13, 21, 22, 101, 111].map(ordinal)).toEqual(["1st", "2nd", "3rd", "4th", "11th", "12th", "13th", "21st", "22nd", "101st", "111th"]);
  });
});

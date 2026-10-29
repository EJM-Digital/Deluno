import { describe, expect, it } from "vitest";
import { buildConnections } from "./connections-model";

const NOW = Date.parse("2026-10-28T10:00:00Z");
const base = { indexers: [], clients: [], clientRows: [], connections: [], metadataMs: null, results: new Map(), now: NOW } as unknown as Parameters<typeof buildConnections>[0];

describe("System: connections model", () => {
  it("shows a processor, media server, Seerr, subtitle or list connection as ok, down or off from its live reading", () => {
    const rows = buildConnections({
      ...base,
      connections: [
        { kind: "processor", id: "w", name: "Weir", enabled: true, healthy: true, countToday: 0, busy: 0 },
        { kind: "mediaServer", id: "p", name: "Plex", enabled: true, healthy: false, countToday: 0, busy: 0 },
        { kind: "requests", id: "s", name: "Seerr", enabled: false, healthy: true, countToday: 0, busy: 0 },
        { kind: "subtitles", id: "o", name: "OpenSubtitles", enabled: true, healthy: true, countToday: 0, busy: 0 },
        { kind: "lists", id: "t", name: "Trakt", enabled: true, healthy: true, countToday: 0, busy: 0 }
      ]
    });
    const state = Object.fromEntries(rows.map((row) => [row.name, row.state]));
    expect(state).toEqual({ Weir: "ok", Plex: "down", Seerr: "off", OpenSubtitles: "ok", Trakt: "ok" });
  });

  it("shows a download client the summary reads as unreachable as down, and one answering as its queue and free space", () => {
    const clients = [
      { id: "q", name: "qBittorrent", isEnabled: true, healthStatus: "healthy", lastHealthLatencyMs: 10 },
      { id: "s", name: "SABnzbd", isEnabled: true, healthStatus: "healthy", lastHealthLatencyMs: 10 }
    ] as never;
    const clientRows = [
      { id: "q", reachable: true, active: 2, queued: 1, freeSpaceBytes: 2_000_000_000 },
      { id: "s", reachable: false, active: 0, queued: 0, freeSpaceBytes: null }
    ] as never;
    const rows = buildConnections({ ...base, clients, clientRows });
    const qb = rows.find((row) => row.name === "qBittorrent")!;
    const sab = rows.find((row) => row.name === "SABnzbd")!;
    expect(sab.state).toBe("down");
    expect(qb.state).toBe("ok");
    expect(qb.sub).toContain("3 items");
    expect(qb.sub).toContain("free");
  });

  it("adds the Deluno metadata row only when the metadata service could be timed: ok under 2.5 s, slow above it, and absent when unread", () => {
    expect(buildConnections({ ...base, metadataMs: null }).find((row) => row.name === "Deluno metadata")).toBeUndefined();
    const fast = buildConnections({ ...base, metadataMs: 100 }).find((row) => row.name === "Deluno metadata")!;
    expect(fast.state).toBe("ok");
    expect(fast.sub).toBe("TMDb and TheTVDB through Deluno’s service");
    const slow = buildConnections({ ...base, metadataMs: 2500 }).find((row) => row.name === "Deluno metadata")!;
    expect(slow.state).toBe("slow");
    expect(slow.sub).toBe("slow to answer");
  });
});

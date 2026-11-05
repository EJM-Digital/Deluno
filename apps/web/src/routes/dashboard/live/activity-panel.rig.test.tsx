import { act, render, renderHook, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { useLiveActivity } from "../live-events";
import { ActivityStream } from "./stream";
import { NO_TITLES } from "./titles";

/**
 * #1491: what the rig wrote between 04:29 and 04:32 UTC on 5 Nov 2026. Three strike and block pairs were
 * hidden while their searches and sends showed. Each row here is a category and message as the server wrote it.
 */
const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../../../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  return {
    ...(await importOriginal<typeof import("../../../lib/use-signalr")>()),
    useSignalREvent: (name: string, _subjects: unknown, handler: (payload: unknown) => void) => {
      const latest = useRef(handler);
      useEffect(() => { latest.current = handler; });
      useEffect(() => {
        const call = (payload: unknown) => latest.current(payload);
        const set = handlers.get(name) ?? new Set();
        set.add(call);
        handlers.set(name, set);
        return () => { set.delete(call); };
      }, [name]);
    }
  };
});

const BARBIE = "Barbie 2023 BluRay 1080p DTS AC3 x264-MgB";
const INSIDE_OUT = "Inside Out 2 2024 1080p BluRay x264 Greek Audio [Braveheart]";

const RIG = [
  { id: "r1", category: "library.search.cycle", message: "Checked Movies: 1 planned, 1 sent, 0 waiting.", createdUtc: "2026-11-05T04:29:10Z", relatedEntityType: null, relatedEntityId: null },
  { id: "r2", category: "download.health.remediated", message: `${BARBIE} (Too slow): It stayed under 1000 KB/s for 1 check, so Deluno stopped waiting for it and removed it.`, createdUtc: "2026-11-05T04:29:40Z", relatedEntityType: "movie", relatedEntityId: "m-barbie" },
  { id: "r3", category: "release.blocked", message: `Deluno blocked ${BARBIE}: Too slow`, createdUtc: "2026-11-05T04:29:41Z", relatedEntityType: "movie", relatedEntityId: "m-barbie" },
  { id: "r4", category: "download.dispatch.recorded", message: "Deluno sent Barbie (2023) 1080p BluRay AV1 Opus Multi4 [dAV1nci] to qBittorrent.", createdUtc: "2026-11-05T04:30:05Z", relatedEntityType: "movie", relatedEntityId: "m-barbie" },
  { id: "r5", category: "download.health.remediated", message: `${INSIDE_OUT} (Dead torrent): Nobody was sharing it, so Deluno stopped waiting for it and removed it.`, createdUtc: "2026-11-05T04:31:50Z", relatedEntityType: "movie", relatedEntityId: "m-inside" },
  { id: "r6", category: "release.blocked", message: `Deluno blocked ${INSIDE_OUT}: Dead torrent`, createdUtc: "2026-11-05T04:31:51Z", relatedEntityType: "movie", relatedEntityId: "m-inside" }
];

const NEWEST_FIRST = [...RIG].reverse();

function panel(rows: ReturnType<typeof useLiveActivity>) {
  render(<MemoryRouter><ActivityStream rows={rows} titles={NO_TITLES} /></MemoryRouter>);
  return screen.getByTestId("live-stream").textContent ?? "";
}

describe("the Activity panel shows what happened to a title (#1491)", () => {
  it("lists the strike, the block, the send and the search round from the rig's own rows", () => {
    const { result } = renderHook(() => useLiveActivity(NEWEST_FIRST));
    const text = panel(result.current);
    expect(text).toContain("Too slow");
    expect(text).toContain("Dead torrent");
    expect(text).toContain(`Deluno blocked ${BARBIE}`);
    expect(text).toContain(`Deluno blocked ${INSIDE_OUT}`);
    expect(text).toContain("Deluno sent Barbie (2023)");
    expect(text).toContain("Checked Movies: 1 planned, 1 sent, 0 waiting.");
  });

  it("reads a strike as a problem, and puts it beside the replacement send, newest first", () => {
    const { result } = renderHook(() => useLiveActivity(NEWEST_FIRST));
    panel(result.current);
    const lines = [...screen.getByTestId("live-stream").querySelectorAll("li")].map((li) => li.textContent ?? "");
    const send = lines.findIndex((line) => line.includes("Deluno sent Barbie"));
    const block = lines.findIndex((line) => line.includes(`blocked ${BARBIE}`));
    const strike = lines.findIndex((line) => line.includes(`${BARBIE} (Too slow)`));
    expect([send, block, strike].every((at) => at >= 0)).toBe(true);
    expect(send).toBeLessThan(block);
    expect(block).toBeLessThan(strike);
    expect(strike - send).toBe(2);
  });

  it("adds each pushed row as it arrives, with the title it concerns, with no reload", () => {
    const { result } = renderHook(() => useLiveActivity([]));
    for (const row of RIG) {
      act(() => {
        for (const handler of handlers.get("ActivityEventAdded") ?? []) handler({ ...row, severity: "info", label: "x" });
      });
    }
    expect(result.current.map((row) => row.id)).toEqual(NEWEST_FIRST.map((row) => row.id));
    expect(result.current[0].relatedEntityId).toBe("m-inside");
    expect(result.current[0].relatedEntityType).toBe("movie");
  });
});

describe("each step in a download's life reaches the panel (#1491)", () => {
  it("shows the rig's 04:36 to 04:48 sequence: a film leaving the queue, a strike, strikes cleared, and a finish, with repeated strikes as one line", () => {
    const at = (minute: string) => `2026-11-05T04:${minute}:00Z`;
    const ROBO = "RoboCop 1987 RM4K 1080p BluRay x264";
    const rows = [
      { id: "l5", category: "download.client.finished", message: `${ROBO} finished downloading in qBittorrent.`, createdUtc: at("47") },
      { id: "l4", category: "download.health.strikes-cleared", message: `${ROBO} is moving again at 3.2 MB/s, strikes cleared.`, createdUtc: at("45") },
      { id: "l3", category: "download.health.strike", message: `${ROBO}: Too slow, strike 2 of 3: stayed under 1000 KB/s`, createdUtc: at("43") },
      { id: "l2", category: "download.health.strike", message: `${ROBO}: Too slow, strike 1 of 3: stayed under 1000 KB/s`, createdUtc: at("41") },
      { id: "l1", category: "download.client.started", message: `Dune (2021) 1080p started downloading in qBittorrent.`, createdUtc: at("36") },
      { id: "l0", category: "download.client.queued", message: `Alien 1979 1080p is waiting in the queue of qBittorrent.`, createdUtc: at("36") }
    ].map((row) => ({ ...row, relatedEntityType: "movie", relatedEntityId: "m1" }));
    const { result } = renderHook(() => useLiveActivity(rows));
    const text = panel(result.current);
    expect(text).toContain("finished downloading");
    expect(text).toContain("strikes cleared");
    expect(text).toContain("strike 2 of 3");
    expect(text).not.toContain("strike 1 of 3");
    expect(text).toContain("started downloading");
    expect(text).toContain("waiting in the queue");
  });
});

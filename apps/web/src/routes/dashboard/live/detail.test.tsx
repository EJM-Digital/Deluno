import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { detailLines, detailRoom, sizePair, spent, type DetailLine } from "./detail";
import type { JourneyCard } from "./journey";
import { LANDING_HOLD_MS, useLandingHold, useStillWhileResizing } from "./motion";

const GB = 1024 ** 3;
const NOW = Date.parse("2026-10-01T10:00:00Z");
const card = (extra: Partial<JourneyCard>): JourneyCard => ({ key: "k", stage: "downloading", title: "Sintel (2010)", state: { tone: "info", label: "Downloading" }, to: "/queue", ...extra });
const text = (line: DetailLine) => line.parts.map((part) => (typeof part === "string" ? part : part.bold)).join("");

afterEach(() => vi.useRealTimers());

describe("a card's detail lines are the mockup's, from real readings", () => {
  it("a download says how much is down, when it should be done, how many peers or connections and the release", () => {
    const lines = detailLines(card({ sizeBytes: 3.2 * GB, downloadedBytes: 1.2 * GB, finishAtMs: new Date("2026-10-01T14:14:00").getTime(), peers: 14, protocol: "torrent", release: "Sintel.2010.1080p.BluRay-NTb" }), 4, NOW);
    expect(lines.map(text)).toEqual(["1.2 of 3.2 GB", "14 peers", "Sintel.2010.1080p.BluRay-NTb"]);
    expect(lines[0].right).toBe("done about 2:14 pm");
    expect(lines[2].mono).toBe(true);
    expect(text(detailLines(card({ peers: 1, protocol: "usenet" }), 4, NOW)[0])).toBe("1 connection");
    expect(text(detailLines(card({ peers: 8, protocol: "usenet" }), 4, NOW)[0])).toBe("8 connections");
  });

  it("works the amount down out of the progress when the client gives no byte count, and leaves a line out when there is no real value", () => {
    expect(text(detailLines(card({ sizeBytes: 4 * GB, progress: 25 }), 4, NOW)[0])).toBe("1.0 of 4.0 GB");
    // No size, no peers, no release, no finish time: nothing is made up
    expect(detailLines(card({}), 4, NOW)).toEqual([]);
  });

  it("a stuck download says only how much it has, and does not claim anything about what Deluno will do", () => {
    const lines = detailLines(card({ stuck: true, sizeBytes: 4 * GB, downloadedBytes: 2 * GB, peers: 3, release: "x", finishAtMs: NOW + 1000 }), 4, NOW);
    expect(lines.map(text)).toEqual(["2.0 of 4.0 GB"]);
    expect(lines[0].right).toBeUndefined();
  });

  it("a grabbed title says its release, its size and where it was sent", () => {
    const lines = detailLines(card({ stage: "grabbed", release: "Sintel.2010.1080p-NTb", sizeBytes: 4.2 * GB, client: "SABnzbd" }), 4, NOW);
    expect(lines.map(text)).toEqual(["Sintel.2010.1080p-NTb", "4.2 GB"]);
    expect(lines[1].right).toBe("to SABnzbd");
  });

  it("a request says who asked and when, and how many indexers it will ask, never the status line's own words", () => {
    const lines = detailLines(card({ stage: "searching", startedBy: "seerr", routeNote: "Asked by Sam", at: new Date(NOW - 5 * 60_000).toISOString() }), 5, NOW);
    expect(lines.map(text)).toEqual(["Asked by Sam", "5 indexers to ask"]);
    expect(lines[0].right).toBe("5 min ago");
    expect(detailLines(card({ stage: "searching", startedBy: "seerr" }), 0, NOW)).toEqual([]);
  });

  it("a title with the processor says the step it reports, the size handed over and for how long, and its place in line", () => {
    const lines = detailLines(card({ stage: "processor", processorStep: "Removing 3 subtitle tracks", sizeBytes: 2.5 * GB, handedOverUtc: new Date(NOW - 4 * 60_000).toISOString(), processorQueuePosition: 2 }), 4, NOW);
    expect(lines.map(text)).toEqual(["Removing 3 subtitle tracks", "2.5 GB handed over", "Number 2 in its line"]);
    expect(lines[1].right).toBe("4 min");
    // No step reported and no size: only what is known
    expect(detailLines(card({ stage: "processor" }), 4, NOW).map(text)).toEqual(["Handed over"]);
  });

  it("an import says where it is going and what it is", () => {
    expect(detailLines(card({ stage: "importing", destination: "TV Shows", sizeBytes: 1.4 * GB, quality: "WEB 1080p" }), 4, NOW).map(text)).toEqual(["To TV Shows", "1.4 GB · WEB 1080p"]);
    expect(detailLines(card({ stage: "importing" }), 4, NOW)).toEqual([]);
  });

  it("a title with the processor or being imported ends on its release name, small and monospaced, like every other card", () => {
    const working = detailLines(card({ stage: "processor", release: "Sintel.2010.1080p-NTb" }), 4, NOW);
    expect(working.map(text)).toEqual(["Handed over", "Sintel.2010.1080p-NTb"]);
    expect(working[1].mono).toBe(true);
    const importing = detailLines(card({ stage: "importing", destination: "Movies", release: "Sintel.2010.1080p-NTb" }), 4, NOW);
    expect(importing.map(text)).toEqual(["To Movies", "Sintel.2010.1080p-NTb"]);
  });

  it("says why Deluno is fetching it as a last line, except for a request that already says so", () => {
    expect(detailLines(card({ stage: "grabbed", release: "x", startedBy: "upgrade" }), 4, NOW).map(text)).toEqual(["x", "Looking for an upgrade"]);
    expect(detailLines(card({ stage: "searching", startedBy: "seerr", routeNote: "Asked by Sam" }), 4, NOW).map(text)).toEqual(["Asked by Sam", "4 indexers to ask"]);
  });

  it("writes two sizes with their unit said once", () => {
    expect(sizePair(1.2 * GB, 3.2 * GB)).toBe("1.2 of 3.2 GB");
    expect(sizePair(400 * 1024 ** 2, 1.5 * GB)).toBe("400 MB of 1.5 GB");
    expect(spent(new Date(NOW - 3 * 60_000).toISOString(), NOW)).toBe("3 min");
    expect(spent(undefined, NOW)).toBeUndefined();
  });

  it("counts whole 15px lines under the card's block, keeping 8px under them", () => {
    expect(detailRoom(419, 400)).toBe(0);
    expect(detailRoom(500, 440)).toBe(3);
    expect(detailRoom(100, 120)).toBe(0);
  });
});

describe("a title whose import has finished is held a moment, full and green, before it leaves", () => {
  it("keeps a landed card for the hold and then drops it, and never holds one that is not landed", () => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    const landed = card({ key: "a", stage: "importing", landed: true });
    const other = card({ key: "b" });
    const { result, rerender } = renderHook(({ cards }) => useLandingHold(cards), { initialProps: { cards: [landed, other] } });
    expect(result.current.map((item) => item.key)).toEqual(["a", "b"]);
    act(() => { vi.advanceTimersByTime(LANDING_HOLD_MS + 100); });
    expect(result.current.map((item) => item.key)).toEqual(["b"]);
    // The same list again does not bring it back
    rerender({ cards: [landed, other] });
    expect(result.current.map((item) => item.key)).toEqual(["b"]);
  });
});

describe("nothing animates while the window is being resized", () => {
  it("marks the page while the size is changing and clears it 350ms after the last change", () => {
    vi.useFakeTimers();
    renderHook(() => useStillWhileResizing());
    expect(document.body.classList.contains("live-resizing")).toBe(false);
    act(() => {
      Object.defineProperty(window, "innerWidth", { configurable: true, value: window.innerWidth + 40 });
      window.dispatchEvent(new Event("resize"));
    });
    expect(document.body.classList.contains("live-resizing")).toBe(true);
    act(() => { vi.advanceTimersByTime(340); });
    expect(document.body.classList.contains("live-resizing")).toBe(true);
    act(() => { vi.advanceTimersByTime(20); });
    expect(document.body.classList.contains("live-resizing")).toBe(false);
  });
});

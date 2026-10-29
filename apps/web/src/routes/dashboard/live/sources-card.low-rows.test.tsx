import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { sortConnections } from "./connections";
import { SourcesCard } from "./sources-card";

const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../../../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  return {
    ...(await importOriginal<typeof import("../../../lib/use-signalr")>()),
    useLiveEvent: (name: string, handler: (payload: unknown) => void) => {
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

beforeEach(() => handlers.clear());
afterEach(cleanup);

const NOW = new Date("2026-10-01T10:00:00");
const indexer = (id: string) => ({ id, name: id, isEnabled: true, priority: 0, healthStatus: "healthy", lastHealthLatencyMs: 320, rateLimitedUntilUtc: null }) as never;
const row = (id: string) => ({ id, name: id, protocol: "torrent", reachable: true, paused: false, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "healthy", active: 0, queued: 0 }) as never;
const connection = (kind: string, name: string, extra: object = {}) => ({ kind, id: name, name, enabled: true, healthy: true, countToday: 1, busy: 0, ...extra }) as never;

function Where() {
  return <span data-testid="where">{useLocation().pathname}</span>;
}

function draw(connections: unknown[]) {
  return render(
    <MemoryRouter initialEntries={["/"]}>
      <SourcesCard
        indexers={[indexer("NZBgeek")]}
        scoreboard={{ indexers: [] } as never}
        summary={undefined}
        now={NOW.getTime()}
        clientRows={[row("qBittorrent")]}
        clients={[{ id: "qBittorrent", name: "qBittorrent", isEnabled: true }] as never}
        speeds={new Map([["qBittorrent", 6.2]])}
        seeding={new Map([["qBittorrent", 8]])}
        connections={sortConnections(connections as never)}
      />
      <Where />
    </MemoryRouter>
  );
}

function send(name: string, payload: unknown) {
  act(() => {
    for (const handler of handlers.get(name) ?? []) handler(payload);
  });
}

describe("Live Sources card (low rows)", () => {
  it("navigates in-app on a plain click of an indexer row and a client legend entry, and leaves ctrl, meta, shift and middle clicks to the browser", () => {
    const { container } = draw([connection("processor", "Weir")]);
    const indexerRow = container.querySelector<HTMLAnchorElement>("a.ls-row")!;
    const legend = container.querySelector<HTMLAnchorElement>(".ls-legend a")!;

    for (const link of [indexerRow, legend]) {
      expect(fireEvent.click(link, { ctrlKey: true })).toBe(true);
      expect(fireEvent.click(link, { metaKey: true })).toBe(true);
      expect(fireEvent.click(link, { shiftKey: true })).toBe(true);
      expect(fireEvent.click(link, { button: 1 })).toBe(true);
      expect(screen.getByTestId("where").textContent).toBe("/");
    }

    expect(fireEvent.click(indexerRow)).toBe(false);
    expect(screen.getByTestId("where").textContent).toBe(indexerRow.getAttribute("href"));
    expect(fireEvent.click(legend)).toBe(false);
    expect(screen.getByTestId("where").textContent).toBe("/indexers/download-clients");
  });

  it("turns a processor pill busy when a hand-off names that processor, or when it is the only processor", () => {
    const { container } = draw([connection("processor", "Weir"), connection("processor", "Cleaner")]);
    const pill = (name: string) => container.querySelector<HTMLAnchorElement>(`a.ls-pill[aria-label^="${name}:"]`)!;
    expect(pill("Weir")).not.toHaveClass("busy");
    expect(pill("Cleaner")).not.toHaveClass("busy");

    send("ProcessorHandoffChanged", { handoffId: "h1", processorName: "Weir", state: "working" });
    expect(pill("Weir")).toHaveClass("busy");
    expect(pill("Cleaner")).not.toHaveClass("busy");

    cleanup();
    handlers.clear();
    const only = draw([connection("processor", "Weir")]);
    const onlyPill = only.container.querySelector<HTMLAnchorElement>("a.ls-pill")!;
    expect(onlyPill).not.toHaveClass("busy");
    send("ProcessorHandoffChanged", { handoffId: "h2", processorName: null, state: "working" });
    expect(onlyPill).toHaveClass("busy");
  });
});

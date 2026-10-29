import { QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { emptySummary } from "./downloads-fixtures";
import { LiveTab } from "./live-tab";

const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  return {
    ...(await importOriginal<typeof import("../../lib/use-signalr")>()),
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
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.resolve({ ok: true, json: async () => ({}) })) }));

const lists: Record<string, unknown> = {};
vi.mock("../../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../../lib/api")>();
  const read = async (path: string) => {
    if (path === "/api/settings") return original.emptyPlatformSettingsSnapshot;
    if (path === "/api/setup/progress") return { lastCompletedStep: 9, isSkipped: false, isCompleted: true, updatedUtc: "2026-09-30T00:00:00Z" };
    const key = Object.keys(lists).find((prefix) => path.startsWith(prefix));
    return key ? lists[key] : [];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

function send(name: string, payload: unknown) {
  act(() => {
    for (const handler of handlers.get(name) ?? []) handler(payload);
  });
}

function seed(capturedUtc: string) {
  lists["/api/download-clients/telemetry"] = {
    capturedUtc,
    summary: {},
    clients: [{
      clientId: "c1", clientName: "Deluge",
      summary: { seedingCount: 7 },
      queue: [
        { id: "q1", clientId: "c1", clientName: "Deluge", title: "Photographer (2026)", releaseName: "Photographer.2026.1080p", status: "downloading", progress: 61, speedMbps: 1.8, etaSeconds: 540, errorMessage: null },
        { id: "q2", clientId: "c1", clientName: "Deluge", title: "Moon (2009)", releaseName: "Moon.2009.1080p", status: "downloading", progress: 20, speedMbps: 2, etaSeconds: 900, errorMessage: null },
        { id: "q3", clientId: "c1", clientName: "Deluge", title: "Solaris (1972)", releaseName: "Solaris.1972.1080p", status: "paused", progress: 5, speedMbps: 9, etaSeconds: null, errorMessage: null }
      ]
    }]
  };
  lists["/api/download-clients"] = [{ id: "c1", name: "Deluge", protocol: "torrent", isEnabled: true }];
}

function renderTab() {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <LiveTab summary={emptySummary} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

beforeEach(() => {
  queryClient.clear();
  handlers.clear();
  for (const key of Object.keys(lists)) delete lists[key];
});

describe("Live: per-client speed (low rows)", () => {
  it("adds the queue's downloading items per client, takes a pushed reading only when newer than the stored one, and reads seeding from the telemetry summary", async () => {
    seed("2026-09-30T10:00:00Z");
    renderTab();
    const clients = screen.getByRole("region", { name: "Download clients" });
    // 1.8 + 2 from the two downloading items; the paused item's 9 is not counted.
    expect(await within(clients).findByLabelText(/Deluge: .*3\.8 MB\/s/)).toBeInTheDocument();
    expect(clients.textContent).toContain("7 seeding");

    // A reading newer than the stored one replaces that item's speed (5.5 + 2).
    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 88, speedMbps: 5.5, eta: null, status: "downloading" });
    await waitFor(() => expect(within(clients).getByLabelText(/Deluge: .*7\.5 MB\/s/)).toBeInTheDocument());
  });

  it("keeps the stored speed when the pushed reading is older than the stored snapshot", async () => {
    seed(new Date(Date.now() + 3_600_000).toISOString());
    renderTab();
    const clients = screen.getByRole("region", { name: "Download clients" });
    expect(await within(clients).findByLabelText(/Deluge: .*3\.8 MB\/s/)).toBeInTheDocument();

    send("DownloadProgress", { id: "q1", title: "Photographer (2026)", progress: 88, speedMbps: 5.5, eta: null, status: "downloading" });
    // A later push is on the screen, so the reading before it has been applied; the client still shows the stored speed.
    send("ActivityEventAdded", { id: "evt-1", message: "Marker row.", category: "searching.cycle.started", label: null, severity: "info", createdUtc: new Date().toISOString() });
    await screen.findAllByText("Marker row.");
    expect(within(clients).getByLabelText(/Deluge: .*3\.8 MB\/s/)).toBeInTheDocument();
  });
});

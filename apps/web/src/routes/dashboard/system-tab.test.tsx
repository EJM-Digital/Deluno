import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { computeAttention } from "./attention";
import { emptySummary, fullSummary } from "./downloads-fixtures";
import { SystemTab } from "./system-tab";

const authedFetchMock = vi.fn();
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

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

function seedBusy() {
  lists["/api/monitoring/machine"] = { hours: 6, samples: [] };
  lists["/api/jobs"] = [{ id: "j2", jobType: "library.scan", status: "failed", lastError: "Out of memory", attempts: 3, maxAttempts: 3, nextAttemptUtc: null }];
  lists["/api/integrations/processors/handoffs"] = [{ id: "h1", releaseName: "Metropolis (1927)", status: "failed", failureMessage: "The processor timed out.", processorName: "Cleaner", updatedUtc: "2026-09-30T09:55:00Z" }];
  lists["/api/indexers"] = [
    { id: "i1", name: "althub.co.za", isEnabled: true, healthStatus: "healthy", lastHealthLatencyMs: 492, rateLimitedUntilUtc: null },
    { id: "i2", name: "Nyaa.si", isEnabled: true, healthStatus: "unreachable", lastHealthLatencyMs: null, rateLimitedUntilUtc: null }
  ];
  lists["/api/integrations/processors/connections"] = [{ id: "p1", name: "Cleaner", isEnabled: true, healthStatus: "unreachable", lastHealthMessage: "connection refused", lastHealthTestUtc: "2026-09-30T09:50:00Z" }];
}

function renderTab(summary: typeof fullSummary | undefined) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <SystemTab summary={summary} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

beforeEach(() => {
  queryClient.clear();
  authedFetchMock.mockReset();
  authedFetchMock.mockResolvedValue({ ok: true, json: async () => ({ message: "Done." }) });
  for (const key of Object.keys(lists)) delete lists[key];
});

describe("System", () => {
  it("shows the clients, indexers and processor with their own buttons, and the system problems", async () => {
    seedBusy();
    renderTab(fullSummary);

    expect(screen.getByText("Needs a look")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Test SABnzbd again" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Resume NZBGet" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Clear NZBGet’s warnings" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Test Nyaa.si again" })).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Test Cleaner again" })).toBeInTheDocument();
    // The processor is named by its own configured name.
    expect(screen.getByRole("heading", { name: "Cleaner" })).toBeInTheDocument();
    expect(screen.queryByText(/weir/i)).toBeNull();

    // The system problems carry their buttons too.
    expect(screen.getByRole("button", { name: "Retry Restart after a crash isn’t set" })).toBeInTheDocument();
    // The indexer that is down is one row with Test again; its health problem is not a second one.
    expect(screen.queryByRole("link", { name: "Fix Nyaa.si isn’t answering" })).toBeNull();
    expect(screen.getByRole("button", { name: "Retry the jobs that gave up" })).toBeInTheDocument();
    expect(screen.getByText(/isn’t running/)).toBeInTheDocument();
    expect(screen.getByText(/1.0.0-rc.24 is ready to install/)).toBeInTheDocument();
    expect(screen.getByText("can’t be read")).toBeInTheDocument();
    expect(screen.getByText("Failed last time")).toBeInTheDocument();

    // The download problems are on Live, not repeated here.
    expect(screen.queryByRole("button", { name: /Give up on/ })).toBeNull();
    expect(screen.queryByText(/leftover folder/)).toBeNull();

    const names = screen.getAllByRole("button").map((button) => button.getAttribute("aria-label") ?? button.textContent);
    expect(new Set(names).size).toBe(names.length);
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN|search-worker|importFailed/);
  });

  it("links every panel to where you act", async () => {
    seedBusy();
    renderTab(fullSummary);
    await screen.findByRole("button", { name: "Test Nyaa.si again" });
    const hrefs = screen.getAllByRole("link").map((link) => link.getAttribute("href"));
    expect(hrefs).toEqual(expect.arrayContaining(["/system", "/system/updates", "/system/backups", "/search-cycles", "/settings/libraries", "/indexers/download-clients", "/indexers/indexers", "/settings/processing"]));
  });

  it("counts exactly what it lists: connections, health problems, jobs that gave up and workers", () => {
    const attention = computeAttention(fullSummary, {
      handoffs: [] as never,
      indexers: [{ id: "i2", name: "Nyaa.si", isEnabled: true, healthStatus: "unreachable" }] as never,
      processors: [{ id: "p1", name: "Cleaner", isEnabled: true, healthStatus: "unreachable" }] as never
    });
    // 2 clients with a button, 1 indexer, 1 processor, 1 health problem not covered by a row, 1 more health problem,
    // 1 row for the jobs that gave up, 1 failing worker. The indexer's health problem is that indexer's row.
    const setup = attention.items.filter((item) => item.tab === "system").reduce((sum, item) => sum + item.count, 0);
    expect(attention.counts.system).toBe(setup);
    expect(attention.counts.system).toBeGreaterThanOrEqual(7);
    expect(computeAttention(emptySummary).counts.system).toBe(0);
  });

  it("sends the same requests the System screen does", async () => {
    seedBusy();
    renderTab(fullSummary);
    const user = userEvent.setup();

    await user.click(screen.getByRole("button", { name: "Retry Restart after a crash isn’t set" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/background-problems/startup%3Aservice-restart-on-failure/retry", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Snooze Restart after a crash isn’t set for a day" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/system%3Abackground-problem%3Astartup%3Aservice-restart-on-failure/snooze", expect.objectContaining({ method: "POST", body: JSON.stringify({ hours: 24 }) })));
    await user.click(screen.getByRole("button", { name: "Retry the jobs that gave up" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/jobs/retry-failed", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Test SABnzbd again" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-clients/c2/test", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Resume NZBGet" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-clients/c3/resume", expect.objectContaining({ method: "POST" })));
  });

  it("says something true on a new install and shows no problem buttons", async () => {
    lists["/api/monitoring/machine"] = { hours: 6, samples: [] };
    renderTab(emptySummary);

    expect(screen.getByText("Ready")).toBeInTheDocument();
    expect(screen.getByText(/Nothing is wrong/)).toBeInTheDocument();
    expect(screen.getByText(/No download client is set up/)).toBeInTheDocument();
    expect(screen.getByText(/No indexer is set up/)).toBeInTheDocument();
    expect(await screen.findByText(/No processor is set up/)).toBeInTheDocument();
    expect(screen.getByText(/No library is set up yet/)).toBeInTheDocument();
    expect(screen.getByText(/No scheduled tasks/)).toBeInTheDocument();
    expect(screen.getByText(/taken a reading of this computer/)).toBeInTheDocument();
    expect(screen.getByText(/No drive has been read yet/)).toBeInTheDocument();
    expect(screen.getByText("no backup yet")).toBeInTheDocument();
    expect(screen.queryAllByRole("button")).toHaveLength(0);
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN/);
  });

  it("renders before the summary has arrived without inventing numbers", () => {
    lists["/api/monitoring/machine"] = { hours: 6, samples: [] };
    const { container } = renderTab(undefined);
    expect(container.textContent).toContain("Starting");
    expect(within(container).queryByText(/NaN/)).toBeNull();
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });
});

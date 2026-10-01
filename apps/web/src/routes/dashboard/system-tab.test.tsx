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

const NOW = "2026-09-30T10:00:00Z";
const check = (over: Record<string, unknown>) => ({ id: "c", category: "system", name: "A check", status: "ok", message: "It is fine.", cause: null, fix: null, detailsJson: null, lastRunUtc: NOW, problemSinceUtc: null, snoozedUntilUtc: null, ...over });

function seedBusy() {
  lists["/api/monitoring/machine"] = {
    hours: 6,
    samples: [0, 1, 2, 3].map((n) => ({
      capturedUtc: `2026-09-30T09:5${n}:00Z`, cpuPercent: 5 + n, memoryBytes: 3.9e9, totalMemoryBytes: 32e9, memoryPercent: 12,
      processReadBytesPerSecond: 1.5e6, processWriteBytesPerSecond: 0.5e6, diskBusyPercent: 10 + n, diskReadBytesPerSecond: 5e6, diskWriteBytesPerSecond: 1e6
    }))
  };
  lists["/api/monitoring/dashboard"] = { performance: { apiLatency: { requestCount: 120, errorCount: 2, errorRatePercent: 1.6, averageMs: 14.2, p95Ms: 61 } } };
  lists["/api/system/health/summary"] = { problemCount: 4, unsnoozedProblemCount: 3, unknownCount: 1, suggestionCount: 1, lastRunUtc: NOW };
  lists["/api/system/health"] = {
    summary: { problemCount: 4, unsnoozedProblemCount: 3, unknownCount: 1, suggestionCount: 1, lastRunUtc: NOW },
    checks: [
      check({ id: "indexer:i2", category: "indexers", name: "Nyaa.si isn’t answering", status: "problem", message: "HTTP 504 since 27 Sep." }),
      check({ id: "system:background-problem:startup:service-restart-on-failure", name: "Restart after a crash isn’t set", status: "problem", message: "Windows won’t restart Deluno by itself.", cause: "The service was installed by hand.", fix: "Set it in Services." }),
      check({ id: "storage:disk", category: "storage", name: "A drive is nearly full", status: "problem", message: "Less than 5% is free." }),
      check({ id: "old:one", name: "An old problem", status: "problem", message: "Still true.", snoozedUntilUtc: "2999-01-01T00:00:00Z" }),
      check({ id: "lib:folders", name: "Library folders", status: "unknown", message: "Not checked yet." }),
      check({ id: "suggest:backups", name: "Turn on backups", status: "suggestion", message: "Backups are off." }),
      check({ id: "db:open", name: "The database opens", status: "ok", message: "Opened in 3 ms." })
    ]
  };
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
  it("lists every health check with its state, the problems first, each problem with its button", async () => {
    seedBusy();
    renderTab(fullSummary);

    const healthPanel = screen.getByRole("region", { name: /^health/i });
    // The stored checks arrive after the summary.
    await within(healthPanel).findByText(/Suggestion. Backups are off./, {}, { timeout: 5000 });
    // Problems: a button that clears each one.
    expect(await within(healthPanel).findByRole("button", { name: "Retry Restart after a crash isn’t set" })).toBeInTheDocument();
    expect(within(healthPanel).getByRole("link", { name: "Fix A drive is nearly full" })).toHaveAttribute("href", "/system");
    expect(within(healthPanel).getByRole("button", { name: "Snooze A drive is nearly full for a day" })).toBeInTheDocument();
    // A connection that is down is one row: its button is in Connections, and it reads that way here.
    expect(within(healthPanel).getByRole("link", { name: "Open Nyaa.si isn’t answering" })).toHaveAttribute("href", "/indexers/indexers");
    expect(within(healthPanel).queryByRole("link", { name: "Fix Nyaa.si isn’t answering" })).toBeNull();
    // What is snoozed, not checked yet, suggested and passing is listed too, in that order after the problems.
    expect(within(healthPanel).getByRole("button", { name: "Unsnooze An old problem" })).toBeInTheDocument();
    expect(within(healthPanel).getByText(/Not checked yet\. Not checked yet\./)).toBeInTheDocument();
    expect(within(healthPanel).getByText(/Suggestion\. Backups are off\./)).toBeInTheDocument();
    const names = within(healthPanel).getAllByRole("listitem").map((item) => item.textContent ?? "");
    const at = (text: string) => names.findIndex((name) => name.includes(text));
    expect(at("A drive is nearly full")).toBeLessThan(at("An old problem"));
    expect(at("An old problem")).toBeLessThan(at("Library folders"));
    expect(at("Library folders")).toBeLessThan(at("Turn on backups"));
    expect(at("Turn on backups")).toBeLessThan(at("The database opens"));
    // Jobs that gave up and a worker that keeps failing have theirs.
    expect(within(healthPanel).getByRole("button", { name: "Retry the jobs that gave up" })).toBeInTheDocument();
    expect(within(healthPanel).getByText(/isn’t running/)).toBeInTheDocument();
  });

  it("shows the connections with their own buttons in one panel", async () => {
    seedBusy();
    renderTab(fullSummary);

    const connections = screen.getByRole("region", { name: /^connections/i });
    expect(within(connections).getByRole("button", { name: "Test SABnzbd again" })).toBeInTheDocument();
    expect(within(connections).getByRole("button", { name: "Resume NZBGet" })).toBeInTheDocument();
    expect(within(connections).getByRole("button", { name: "Clear NZBGet’s warnings" })).toBeInTheDocument();
    expect(await within(connections).findByRole("button", { name: "Test Nyaa.si again" })).toBeInTheDocument();
    // The processor is named by its own configured name.
    expect(await within(connections).findByRole("button", { name: "Test Cleaner again" })).toBeInTheDocument();
    expect(within(connections).getByText("Cleaner")).toBeInTheDocument();
    expect(screen.queryByText(/weir/i)).toBeNull();
  });

  it("shows the machine: processor, memory, how busy the drive is, Deluno's disk use, API speed, and each drive's size", async () => {
    seedBusy();
    renderTab(fullSummary);

    const machine = screen.getByRole("region", { name: /^this computer/i });
    expect(within(machine).getByText("CPU")).toBeInTheDocument();
    expect(within(machine).getByText("Memory")).toBeInTheDocument();
    expect(await within(machine).findByText("Drive busy")).toBeInTheDocument();
    expect(within(machine).getByText("Deluno’s disk use")).toBeInTheDocument();
    await waitFor(() => expect(machine.textContent).toContain("reading 1.4 MB/s, writing 488 KB/s"));
    // The strip says how quickly the API answers, once, as a headline; the panel says how many requests that rests on.
    expect(await screen.findByText("14 ms")).toBeInTheDocument();
    expect(screen.getByText("61 ms for the slowest 5%")).toBeInTheDocument();
    expect(within(machine).getByText("120, 2 failed")).toBeInTheDocument();

    const storage = screen.getByRole("region", { name: /^storage/i });
    expect(within(storage).getByText(/1.4 TB free of 1.8 TB/)).toBeInTheDocument();
    expect(within(storage).getByText("can’t be read")).toBeInTheDocument();
    expect(screen.getByText(/1.0.0-rc.24 is ready to install/)).toBeInTheDocument();
    expect(screen.getByText("Failed last time")).toBeInTheDocument();
  });

  it("counts exactly what it lists: connections, health problems, jobs that gave up and workers", () => {
    const attention = computeAttention(fullSummary, {
      handoffs: [] as never,
      indexers: [{ id: "i2", name: "Nyaa.si", isEnabled: true, healthStatus: "unreachable" }] as never,
      processors: [{ id: "p1", name: "Cleaner", isEnabled: true, healthStatus: "unreachable" }] as never
    });
    const system = attention.items.filter((item) => item.tab === "system").reduce((sum, item) => sum + item.count, 0);
    expect(attention.counts.system).toBe(system);
    expect(attention.counts.system).toBeGreaterThanOrEqual(7);
    expect(computeAttention(emptySummary).counts.system).toBe(0);
  });

  it("sends the same requests the System screen does", async () => {
    seedBusy();
    renderTab(fullSummary);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Retry Restart after a crash isn’t set" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/background-problems/startup%3Aservice-restart-on-failure/retry", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Snooze A drive is nearly full for a day" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/storage%3Adisk/snooze", expect.objectContaining({ method: "POST", body: JSON.stringify({ hours: 24 }) })));
    await user.click(screen.getByRole("button", { name: "Unsnooze An old problem" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/old%3Aone/unsnooze", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Retry the jobs that gave up" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/jobs/retry-failed", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Test SABnzbd again" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-clients/c2/test", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Resume NZBGet" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-clients/c3/resume", expect.objectContaining({ method: "POST" })));
  });

  it("has unique button names, plain words, and leaves the download problems to Live", async () => {
    seedBusy();
    renderTab(fullSummary);
    await screen.findByRole("button", { name: "Test Nyaa.si again" });
    const names = screen.getAllByRole("button").map((button) => button.getAttribute("aria-label") ?? button.textContent);
    expect(new Set(names).size).toBe(names.length);
    expect(screen.queryByRole("button", { name: /Give up on/ })).toBeNull();
    expect(screen.queryByText(/leftover folder/)).toBeNull();
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN|search-worker|importFailed/);
  });

  it("links every panel to where you act", async () => {
    seedBusy();
    renderTab(fullSummary);
    await screen.findByRole("button", { name: "Test Nyaa.si again" });
    const hrefs = screen.getAllByRole("link").map((link) => link.getAttribute("href"));
    expect(hrefs).toEqual(expect.arrayContaining(["/system", "/system/updates", "/system/backups", "/search-cycles", "/settings/libraries", "/indexers/download-clients", "/indexers/indexers"]));
  });

  it("says something true on a new install and shows no problem buttons", async () => {
    lists["/api/monitoring/machine"] = { hours: 6, samples: [] };
    lists["/api/system/health"] = { checks: [], summary: { problemCount: 0, unsnoozedProblemCount: 0, unknownCount: 0, suggestionCount: 0, lastRunUtc: null } };
    renderTab(emptySummary);

    expect(screen.getByText("Ready")).toBeInTheDocument();
    expect(await screen.findByText(/Nothing is wrong/)).toBeInTheDocument();
    expect(screen.getByText(/No download client is set up/)).toBeInTheDocument();
    expect(screen.getByText(/No indexer is set up/)).toBeInTheDocument();
    expect(screen.getByText(/No processor is set up/)).toBeInTheDocument();
    expect(screen.getByText(/No library is set up yet/)).toBeInTheDocument();
    expect(screen.getByText(/No scheduled tasks/)).toBeInTheDocument();
    expect(screen.getByText(/taken a reading of this computer/)).toBeInTheDocument();
    expect(screen.getByText(/No drive has been read yet/)).toBeInTheDocument();
    expect(screen.getByText("no backup yet")).toBeInTheDocument();
    expect(screen.getByText("no requests measured yet")).toBeInTheDocument();
    // The library counts moved here from Live: nothing in the library yet says so, once for the films and once for the shows.
    expect(screen.getAllByText("none yet").length).toBeGreaterThan(0);
    expect(screen.getByRole("region", { name: /^library/i })).toBeInTheDocument();
    expect(screen.queryAllByRole("button")).toHaveLength(0);
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN/);
  });

  it("renders before the summary has arrived without inventing numbers", () => {
    lists["/api/monitoring/machine"] = { hours: 6, samples: [] };
    const { container } = renderTab(undefined);
    expect(container.textContent).toContain("Starting");
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });
});

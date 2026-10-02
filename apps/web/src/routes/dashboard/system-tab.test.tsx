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
  it("lists the problems first with an area tag and two short buttons, and the rest under the area", async () => {
    seedBusy();
    renderTab(fullSummary);
    const health = screen.getByRole("region", { name: "Health" });
    expect(await within(health).findByRole("button", { name: "Try again: Restart after a crash isn’t set" })).toBeInTheDocument();
    expect(await within(health).findByRole("link", { name: "Open System: A drive is nearly full" })).toHaveAttribute("href", "/system");
    expect(within(health).getByRole("button", { name: "Snooze 1 day: A drive is nearly full" })).toHaveAttribute("title", expect.stringContaining("not fixed or changed"));
    expect(within(health).getByRole("button", { name: "Retry: 1 background job gave up" })).toBeInTheDocument();
    expect(within(health).getByRole("button", { name: "Unsnooze: An old problem" })).toBeInTheDocument();
    // An area lists all of its checks, the passing ones too.
    const user = userEvent.setup();
    await user.click(within(health).getByRole("button", { name: /^Machine/ }));
    expect(within(health).getByText("Library folders")).toBeInTheDocument();
  });

  it("jumps to Health from the number on This instance", async () => {
    seedBusy();
    renderTab(fullSummary);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("link", { name: /need you →/ }));
    await waitFor(() => expect(screen.getByRole("region", { name: "Health" }).className).toContain("sy-flash"));
  });

  it("shows every connection with its state, and tests one on request", async () => {
    seedBusy();
    renderTab(fullSummary);
    const connections = screen.getByRole("region", { name: "Connections" });
    expect(await within(connections).findByText("Nyaa.si")).toBeInTheDocument();
    const user = userEvent.setup();
    authedFetchMock.mockResolvedValue({ ok: true, json: async () => ({ healthStatus: "healthy", message: "ok" }) });
    await user.click(within(connections).getByRole("button", { name: "Test althub.co.za" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/indexers/i1/test", expect.objectContaining({ method: "POST" })));
  });

  it("Test all asks the server to test every connection at once and shows each answer (#1098)", async () => {
    seedBusy();
    renderTab(fullSummary);
    const connections = screen.getByRole("region", { name: "Connections" });
    expect(await within(connections).findByText("Nyaa.si")).toBeInTheDocument();
    const user = userEvent.setup();
    authedFetchMock.mockResolvedValue({
      ok: true,
      json: async () => ({ tested: 2, results: [
        { kind: "indexer", id: "i2", name: "Nyaa.si", ok: true, slow: false, message: "Answered.", checkedUtc: new Date().toISOString() },
        { kind: "indexer", id: "i1", name: "althub.co.za", ok: false, slow: false, message: "Timed out.", checkedUtc: new Date().toISOString() }
      ] })
    });

    await user.click(within(connections).getByRole("button", { name: "Test all connections" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/connections/test-all", expect.objectContaining({ method: "POST" })));
    // One request for everything; the indexers are not each tested by hand as well.
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/indexers/i1/test", expect.anything());
    expect(await within(connections).findByText("Timed out.")).toBeInTheDocument();
  });

  it("draws the machine from the overview and leaves out what it could not read", async () => {
    seedBusy();
    lists["/api/system/overview"] = {
      instance: { version: "1.0.0-rc.24", latest: true, channel: "stable", startedUtc: "2026-09-30T08:00:00Z", address: "http://192.168.1.51:5099", runsAs: "service", cpuPercent: 1, memoryBytes: 405e6, workersBusy: 0, oldestWaitingSeconds: null, answerMs: 4, answerSlowestMs: 15, jobsToday: 412, jobsFailedToday: 1, requestsToday: 8214, requestErrorsToday: 0, dataBytes: 387e6, databaseCount: 5, lastBackupUtc: null, lastBackupBytes: null, restartsThisWeek: 0, browsersLive: 2 },
      machine: { os: "Windows 11", uptimeSeconds: 86400, rebootPending: null, cpuTemperatureC: null, cpuPercent: 2, cores: 8, memoryUsedBytes: 7.4e9, memoryTotalBytes: 31.3e9, diskReadBytesPerSec: 0, diskWriteBytesPerSec: 0, diskIops: 0 },
      network: { adapter: "Ethernet", linkMbps: 1000, ip: "192.168.1.196", inBytesPerSec: 0, outBytesPerSec: 0, internet: true, dnsLookupMs: null, nasMs: 2, nasName: null, metadataMs: null, clockInSync: true },
      drives: [], history: { cpu: [], memory: [], disk: [], netIn: [] }
    };
    renderTab(fullSummary);
    expect(await screen.findByText("1.0.0-rc.24")).toBeInTheDocument();
    expect(screen.getByText("192.168.1.51")).toBeInTheDocument();
    expect(screen.getByText("port 5099")).toBeInTheDocument();
    const machine = screen.getByRole("region", { name: "This computer" });
    expect(within(machine).queryByText(/reboot/)).toBeNull();
    expect(within(machine).queryByText(/°C/)).toBeNull();
    const network = screen.getByRole("region", { name: "Network" });
    expect(within(network).queryByText("DNS lookup")).toBeNull();
    expect(within(network).getByText("Your NAS")).toBeInTheDocument();
  });

  it("works without the overview: the summary's own facts and no invented numbers", async () => {
    seedBusy();
    const { container } = renderTab(fullSummary);
    expect(await screen.findByText("Version")).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/undefined|NaN/);
    expect(container.textContent).not.toContain("Browsers");
  });

  it("has unique button names, plain words and no Library card", async () => {
    seedBusy();
    renderTab(fullSummary);
    await screen.findByRole("button", { name: "Test althub.co.za" });
    const names = screen.getAllByRole("button").map((button) => button.getAttribute("aria-label") ?? button.textContent);
    expect(new Set(names).size).toBe(names.length);
    expect(screen.queryByRole("region", { name: /^library/i })).toBeNull();
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN|importFailed/);
  });

  it("counts exactly what it lists", () => {
    const attention = computeAttention(fullSummary, { handoffs: [] as never, indexers: [{ id: "i2", name: "Nyaa.si", isEnabled: true, healthStatus: "unreachable" }] as never });
    expect(attention.counts.system).toBe(attention.items.filter((item) => item.tab === "system").reduce((sum, item) => sum + item.count, 0));
    expect(computeAttention(emptySummary).counts.system).toBe(0);
  });

  it("says something true on a new install", async () => {
    lists["/api/system/health"] = { checks: [], summary: { problemCount: 0, unsnoozedProblemCount: 0, unknownCount: 0, suggestionCount: 0, lastRunUtc: null } };
    const { container } = renderTab(emptySummary);
    expect(await screen.findByText(/Nothing is wrong/)).toBeInTheDocument();
    expect(screen.getByText(/No scheduled tasks/)).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });

  it("renders before the summary has arrived", () => {
    const { container } = renderTab(undefined);
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });
});

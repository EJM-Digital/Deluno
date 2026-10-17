import { QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { HealthCheckResultItem } from "../../../lib/api";
import { emptyAttentionSummary } from "../../../lib/attention-summary";
import { serverRow, summaryOf } from "../../../lib/attention-summary-fixtures";
import { queryClient } from "../../../lib/route-cache";
import { HealthCard } from "./health-card";
import { buildHealth } from "./problems";

const authedFetchMock = vi.fn();
vi.mock("../../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../../../components/shell/toaster", () => ({ toast }));

const NOW = "2026-10-15T01:00:00Z";
const check = (over: Partial<HealthCheckResultItem> & { id: string; name: string }): HealthCheckResultItem => ({
  category: "system", status: "problem", message: "It is wrong.", cause: null, fix: null, detailsJson: null, lastRunUtc: NOW, problemSinceUtc: NOW, snoozedUntilUtc: null, ...over
}) as HealthCheckResultItem;

function renderCard(checks: HealthCheckResultItem[]) {
  const model = buildHealth({ summary: undefined, attention: emptyAttentionSummary, checks });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <HealthCard model={model} hasChecks jump={0} now={Date.parse(NOW)} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const ok = (body: unknown = {}) => ({ ok: true, status: 200, json: async () => body, text: async () => "" });

describe("the Health card's buttons", () => {
  beforeEach(() => {
    queryClient.clear();
    authedFetchMock.mockReset();
    toast.success.mockReset();
    toast.error.mockReset();
  });

  const BUTTONS: { label: string; check: HealthCheckResultItem; method: string; path: string; body?: unknown; done: string }[] = [
    { label: "Try again", check: check({ id: "system:background-problem:startup:service-restart-on-failure", name: "Restart after a crash isnâ€™t set" }), method: "POST", path: "/api/system/health/background-problems/startup%3Aservice-restart-on-failure/retry", done: "Tried again" },
    { label: "Snooze 1 day", check: check({ id: "storage:disk", name: "A drive is nearly full", category: "storage" }), method: "POST", path: "/api/system/health/storage%3Adisk/snooze", body: { hours: 24 }, done: "Snoozed for a day" },
    { label: "Keep awake", check: check({ id: "machine:sleep", name: "This computer may sleep", category: "machine" }), method: "PATCH", path: "/api/settings", body: { keepComputerAwake: true }, done: "Deluno is keeping this computer awake" },
    { label: "Back up now", check: check({ id: "machine:drive:C:", name: "Drive C: is nearly full", category: "machine" }), method: "POST", path: "/api/backups", done: "Backup started" },
    { label: "Test again", check: check({ id: "processor:p1", name: "Cleaner isnâ€™t answering", category: "connections" }), method: "POST", path: "/api/connections/processor/p1/test", done: "Tested again: Cleaner isnâ€™t answering" },
    { label: "Switch off", check: check({ id: "indexer:i2", name: "Nyaa.si isn’t answering", category: "indexers" }), method: "PUT", path: "/api/indexers/i2", body: { isEnabled: false }, done: "Nyaa.si isn’t answering switched off" },
    { label: "Unsnooze", check: check({ id: "old:one", name: "An old problem", snoozedUntilUtc: "2999-01-01T00:00:00Z" }), method: "POST", path: "/api/system/health/old%3Aone/unsnooze", done: "An old problem is being watched again" }
  ];

  it.each(BUTTONS)("pressing $label sends its own request and says what happened in a toast", async (button) => {
    authedFetchMock.mockResolvedValue(ok());
    renderCard([button.check]);

    fireEvent.click(await screen.findByRole("button", { name: `${button.label}: ${button.check.name}` }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith(button.done));
    const [path, init] = authedFetchMock.mock.calls[0];
    expect(path).toBe(button.path);
    expect(init.method).toBe(button.method);
    if (button.body !== undefined) expect(JSON.parse(init.body)).toEqual(button.body);
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("re-reads what is on screen once an action is done", async () => {
    authedFetchMock.mockResolvedValue(ok());
    const refresh = vi.spyOn(queryClient, "invalidateQueries");
    renderCard([BUTTONS[1].check]);

    fireEvent.click(await screen.findByRole("button", { name: "Snooze 1 day: A drive is nearly full" }));

    await waitFor(() => expect(refresh).toHaveBeenCalled());
  });

  it("shows the server's own words when it says no, and a plain failure when it says nothing", async () => {
    authedFetchMock.mockResolvedValueOnce({ ok: false, status: 409, json: async () => ({ message: "Another backup is already running." }), text: async () => JSON.stringify({ message: "Another backup is already running." }) });
    renderCard([BUTTONS[3].check]);

    fireEvent.click(await screen.findByRole("button", { name: "Back up now: Drive C: is nearly full" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Another backup is already running."));

    authedFetchMock.mockResolvedValueOnce({ ok: false, status: 500, json: async () => { throw new Error("no body"); }, text: async () => "" });
    fireEvent.click(screen.getByRole("button", { name: "Back up now: Drive C: is nearly full" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("The backup could not be started."));
    expect(toast.success).not.toHaveBeenCalled();
  });

  it("does not send the same request twice while it is still running", async () => {
    let finish: (value: unknown) => void = () => undefined;
    authedFetchMock.mockReturnValue(new Promise((resolve) => { finish = resolve; }));
    renderCard([BUTTONS[3].check]);
    const button = await screen.findByRole("button", { name: "Back up now: Drive C: is nearly full" });

    fireEvent.click(button);
    fireEvent.click(button);

    await waitFor(() => expect(button).toHaveAttribute("aria-disabled", "true"));
    expect(authedFetchMock).toHaveBeenCalledTimes(1);
    await act(async () => { finish(ok()); });
    await waitFor(() => expect(button).toHaveAttribute("aria-disabled", "false"));
  });

  // One request at a time: while any health button runs, every health button is disabled (#1341).
  it("disables the other buttons while one request is running", async () => {
    let finish: (value: unknown) => void = () => undefined;
    authedFetchMock.mockReturnValue(new Promise((resolve) => { finish = resolve; }));
    renderCard([BUTTONS[3].check]);

    fireEvent.click(await screen.findByRole("button", { name: "Back up now: Drive C: is nearly full" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Snooze 1 day: Drive C: is nearly full" })).toHaveAttribute("aria-disabled", "true"));
    await act(async () => { finish(ok()); });
  });
});

describe("the Health card's other buttons", () => {
  beforeEach(() => {
    queryClient.clear();
    authedFetchMock.mockReset();
    toast.success.mockReset();
    toast.error.mockReset();
  });

  it("Retry on jobs that gave up puts them back in the queue and says so", async () => {
    authedFetchMock.mockResolvedValue(ok());
    const model = buildHealth({ summary: undefined, attention: summaryOf([serverRow({ id: "failed-jobs", kind: "jobs", tone: "bad", title: "1 background job gave up", text: "The work was not done." })]), checks: [] });
    render(<QueryClientProvider client={queryClient}><MemoryRouter><HealthCard model={model} hasChecks jump={0} now={Date.parse(NOW)} /></MemoryRouter></QueryClientProvider>);

    fireEvent.click(await screen.findByRole("button", { name: "Retry: 1 background job gave up" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Failed jobs requeued"));
    expect(authedFetchMock.mock.calls[0][0]).toBe("/api/jobs/retry-failed");
    expect(authedFetchMock.mock.calls[0][1].method).toBe("POST");
  });

  it("an Open button is a link to the screen that clears the problem and sends no request", async () => {
    renderCard([check({ id: "storage:disk", name: "A drive is nearly full", category: "storage" })]);

    const open = await screen.findByRole("link", { name: "Open System: A drive is nearly full" });

    expect(open).toHaveAttribute("href", "/system");
    fireEvent.click(open);
    expect(authedFetchMock).not.toHaveBeenCalled();
  });
});

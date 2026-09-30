import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { emptySummary, fullSummary } from "./downloads-fixtures";
import { SystemTab, systemTabAttention } from "./system-tab";

const authedFetchMock = vi.fn();
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const lists: Record<string, unknown> = {};
vi.mock("../../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../../lib/api")>();
  const read = async (path: string) => {
    const key = Object.keys(lists).find((prefix) => path.startsWith(prefix));
    if (!key) throw new Error(`No fixture for ${path}`);
    return lists[key];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

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

describe("System tab", () => {
  it("counts problems, failed jobs and failing workers, and nothing when all is well", () => {
    // 3 health problems + 2 jobs that gave up + 1 failing worker
    expect(systemTabAttention(fullSummary)).toBe(6);
    expect(systemTabAttention(emptySummary)).toBe(0);
    expect(systemTabAttention(undefined)).toBe(0);
  });

  it("shows problems with their buttons, plain-words activity and links to where you act", async () => {
    lists["/api/monitoring/machine"] = { hours: 6, samples: [] };
    lists["/api/activity"] = [{ id: "a1", category: "filesystem.import.completed", label: "Imported into the library", message: "Imported Sintel (2010).", createdUtc: "2026-09-30T09:58:00Z", detailsJson: null, relatedJobId: null, relatedEntityType: null, relatedEntityId: null }];
    renderTab(fullSummary);

    expect(screen.getByText("Needs a look")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry Restart after a crash isn’t set" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Fix Nyaa.si isn’t answering" })).toHaveAttribute("href", "/indexers/indexers");
    expect(screen.getByRole("button", { name: "Retry the jobs that gave up" })).toBeInTheDocument();
    expect(screen.getByText(/isn’t running/)).toBeInTheDocument();
    expect(screen.getByText(/1.0.0-rc.24 is ready to install/)).toBeInTheDocument();
    expect(screen.getByText("can’t be read")).toBeInTheDocument();
    expect(screen.getByText("Failed last time")).toBeInTheDocument();
    expect(screen.getByText("Not run yet")).toBeInTheDocument();

    // Activity shows the plain label, never the raw kind.
    expect(await screen.findByText(/Imported into the library/)).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/filesystem\.import|undefined|NaN|search-worker/);

    const hrefs = screen.getAllByRole("link").map((link) => link.getAttribute("href"));
    expect(hrefs).toEqual(expect.arrayContaining(["/system", "/system/updates", "/system/backups", "/activity", "/search-cycles", "/settings/libraries"]));

    const names = screen.getAllByRole("button").map((button) => button.getAttribute("aria-label") ?? button.textContent);
    expect(new Set(names).size).toBe(names.length);
  });

  it("sends the same requests the System screen does", async () => {
    lists["/api/monitoring/machine"] = { hours: 6, samples: [] };
    lists["/api/activity"] = [];
    renderTab(fullSummary);
    const user = userEvent.setup();

    await user.click(screen.getByRole("button", { name: "Retry Restart after a crash isn’t set" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/background-problems/startup%3Aservice-restart-on-failure/retry", expect.objectContaining({ method: "POST" })));
    await user.click(screen.getByRole("button", { name: "Snooze Nyaa.si isn’t answering for a day" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/indexer%3Ai2/snooze", expect.objectContaining({ method: "POST", body: JSON.stringify({ hours: 24 }) })));
    await user.click(screen.getByRole("button", { name: "Retry the jobs that gave up" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/jobs/retry-failed", expect.objectContaining({ method: "POST" })));
  });

  it("says something true on an empty install and shows no problem buttons", async () => {
    lists["/api/monitoring/machine"] = { hours: 6, samples: [] };
    lists["/api/activity"] = [];
    renderTab(emptySummary);

    expect(screen.getByText("Ready")).toBeInTheDocument();
    expect(screen.getByText(/Nothing is wrong/)).toBeInTheDocument();
    expect(screen.getByText(/No library is set up yet/)).toBeInTheDocument();
    expect(screen.getByText(/No scheduled tasks/)).toBeInTheDocument();
    expect(screen.getByText(/taken a reading of this computer/)).toBeInTheDocument();
    expect(screen.getByText(/No drive has been read yet/)).toBeInTheDocument();
    expect(await screen.findByText(/Nothing has happened yet/)).toBeInTheDocument();
    expect(screen.getByText("no backup yet")).toBeInTheDocument();
    expect(document.body.textContent ?? "").not.toMatch(/undefined|NaN/);
  });

  it("renders before the summary has arrived without inventing numbers", () => {
    lists["/api/monitoring/machine"] = { hours: 6, samples: [] };
    lists["/api/activity"] = [];
    const { container } = renderTab(undefined);
    expect(container.textContent).toContain("Starting");
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });
});

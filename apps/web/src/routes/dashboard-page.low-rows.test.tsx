import { QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation, useNavigate } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { fullSummary } from "./dashboard/summary-fixtures";

const api = vi.hoisted(() => ({ fetchJson: vi.fn(), summary: vi.fn(), resync: { all: [] as Array<() => void> } }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: api.fetchJson
}));
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.resolve(new Response("{}"))) }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useSignalRResync: (callback: () => void) => { api.resync.all.push(callback); }
}));

const { DashboardPage } = await import("./dashboard-page");
const { queryClient } = await import("../lib/route-cache");

function Where() {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <output aria-label="address">{location.pathname + location.search}</output>
      <button type="button" onClick={() => navigate(-1)}>Browser back</button>
    </>
  );
}

function open(initial = "/") {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initial]}>
        <DashboardPage />
        <Where />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

beforeEach(() => {
  queryClient.clear();
  api.fetchJson.mockReset();
  api.resync.all = [];
  api.summary.mockReturnValue(Promise.resolve(fullSummary()));
  api.fetchJson.mockImplementation((path: string) => {
    if (path === "/api/dashboard/summary") return api.summary();
    if (path === "/api/settings") return Promise.resolve(emptyPlatformSettingsSnapshot);
    if (path === "/api/setup/progress") return Promise.resolve({ lastCompletedStep: 0, isSkipped: false, isCompleted: false, updatedUtc: "2026-09-30T00:00:00Z" });
    if (path.startsWith("/api/dashboard/metrics")) return Promise.resolve({ days: 30, librarySize: [], titlesAdded: [] });
    if (path.endsWith("/wanted")) return Promise.resolve({ totalWanted: 0, missingCount: 0, upgradeCount: 0, coveredCount: 0, upcomingCount: 0, recentItems: [] });
    return Promise.resolve([]);
  });
});

describe("the Dashboard shell (low rows)", () => {
  it("the tab list is labelled 'Dashboard sections', each tab controls the panel and the panel is labelled by the chosen tab", async () => {
    const user = userEvent.setup();
    open();

    const tablist = await screen.findByRole("tablist", { name: "Dashboard sections" });
    for (const tab of within(tablist).getAllByRole("tab")) expect(tab).toHaveAttribute("aria-controls", "dashboard-tabpanel");
    const panel = screen.getByRole("tabpanel");
    expect(panel).toHaveAttribute("id", "dashboard-tabpanel");
    expect(panel).toHaveAttribute("aria-labelledby", "dashboard-tab-live");

    await user.click(screen.getByRole("tab", { name: /^System/ }));
    expect(screen.getByRole("tabpanel")).toHaveAttribute("aria-labelledby", "dashboard-tab-system");
  });

  it("each tab switch adds a history entry, so Back returns to the previous tab", async () => {
    const user = userEvent.setup();
    open();
    await screen.findByRole("tablist", { name: "Dashboard sections" });

    await user.click(screen.getByRole("tab", { name: /^System/ }));
    expect(screen.getByLabelText("address").textContent).toBe("/?tab=system");

    await user.click(screen.getByRole("button", { name: "Browser back" }));

    expect(screen.getByLabelText("address").textContent).toBe("/");
    expect(screen.getByRole("tab", { name: /^Live/ })).toHaveAttribute("aria-selected", "true");
  });

  it("before the first summary arrives the skeleton is shown and no tab is drawn", async () => {
    api.summary.mockReturnValue(new Promise(() => undefined));
    const { container } = open();

    await waitFor(() => expect(container.querySelector("[data-route-skeleton]")).not.toBeNull());
    expect(screen.queryByRole("tab")).toBeNull();
  });

  it("a failed refresh after the page has data keeps the last answer on screen", async () => {
    open();
    await screen.findByRole("tablist", { name: "Dashboard sections" });

    api.summary.mockReturnValue(Promise.reject(new Error("The server did not answer.")));
    await act(async () => { await queryClient.invalidateQueries({ queryKey: ["dashboard"] }).catch(() => undefined); });
    await waitFor(() => expect(api.fetchJson.mock.calls.filter(([path]) => path === "/api/dashboard/summary").length).toBeGreaterThan(1));

    expect(screen.getByRole("tablist", { name: "Dashboard sections" })).toBeInTheDocument();
    expect(screen.queryByText("The Dashboard could not load")).toBeNull();
  });

  it("after a dropped live connection the Dashboard asks again once: the summary and the setup lists", async () => {
    open();
    await screen.findByRole("tablist", { name: "Dashboard sections" });
    expect(api.resync.all.length).toBeGreaterThan(0);
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");

    const newest = api.resync.all.slice(-8);
    act(() => { for (const callback of newest) callback(); });

    const keys = invalidate.mock.calls.map(([filter]) => (filter as { queryKey: unknown[] }).queryKey[0]);
    expect(keys).toEqual(expect.arrayContaining(["dashboard", "src"]));
    invalidate.mockRestore();
  });
});

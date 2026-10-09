import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { emptySummary, fullSummary } from "./dashboard/summary-fixtures";

const api = vi.hoisted(() => ({ fetchJson: vi.fn(), summary: vi.fn() }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: api.fetchJson
}));
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.resolve(new Response("{}"))) }));

const { DashboardPage, dashboardShouldRevalidate, tabFromSearch } = await import("./dashboard-page");
const { queryClient } = await import("../lib/route-cache");

function Where() {
  const location = useLocation();
  return <output aria-label="address">{location.pathname + location.search}</output>;
}

function open(initial: string) {
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
  api.summary.mockReturnValue(fullSummary());
  api.fetchJson.mockImplementation((path: string) => {
    if (path === "/api/dashboard/summary") return Promise.resolve(api.summary());
    if (path === "/api/settings") return Promise.resolve(emptyPlatformSettingsSnapshot);
    if (path === "/api/setup/progress") return Promise.resolve({ lastCompletedStep: 0, isSkipped: false, isCompleted: false, updatedUtc: "2026-09-30T00:00:00Z" });
    if (path.startsWith("/api/dashboard/metrics")) return Promise.resolve({ days: 30, librarySize: [], titlesAdded: [] });
    if (path.endsWith("/wanted")) return Promise.resolve({ totalWanted: 0, missingCount: 0, upgradeCount: 0, coveredCount: 0, upcomingCount: 0, recentItems: [] });
    return Promise.resolve([]);
  });
});

describe("tabFromSearch", () => {
  it("reads the tab from the address, and anything else is Live", () => {
    expect(tabFromSearch("?tab=system")).toBe("system");
    expect(tabFromSearch("?tab=now")).toBe("live");
    expect(tabFromSearch("")).toBe("live");
    expect(tabFromSearch("?tab=nonsense")).toBe("live");
  });

  it("opens the tab an older link's content moved to", () => {
    expect(tabFromSearch("?tab=overview")).toBe("live");
    expect(tabFromSearch("?tab=library")).toBe("live");
    expect(tabFromSearch("?tab=downloads")).toBe("live");
    expect(tabFromSearch("?tab=setup-health")).toBe("system");
    expect(tabFromSearch("?tab=system")).toBe("system");
  });
});

describe("DashboardPage tabs", () => {
  it("has exactly two tabs, opens on Live and switches through the address, with no reload", async () => {
    const user = userEvent.setup();
    open("/");

    expect(await screen.findByRole("region", { name: /^pipeline/i })).toBeInTheDocument();
    expect(screen.getAllByRole("tab").map((tab) => tab.textContent?.replace(/[0-9]+$/, ""))).toEqual(["Live", "System"]);
    expect(screen.getByRole("tab", { name: /^Live/ })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByLabelText("address").textContent).toBe("/");

    await user.click(screen.getByRole("tab", { name: /^System/ }));
    expect(screen.getByLabelText("address").textContent).toBe("/?tab=system");
    expect(screen.getByRole("tab", { name: /^System/ })).toHaveAttribute("aria-selected", "true");
    expect(await screen.findByRole("region", { name: /^health/i })).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: /^pipeline/i })).toBeNull();

    // Live carries no tab at all.
    await user.click(screen.getByRole("tab", { name: /^Live/ }));
    expect(screen.getByLabelText("address").textContent).toBe("/");
    // Switching tabs never asked for the summary again: it was already there.
    expect(api.fetchJson.mock.calls.filter(([path]) => path === "/api/dashboard/summary")).toHaveLength(1);
  });

  it("can be linked straight to a tab, and an old link opens the tab its content moved to", async () => {
    const first = open("/?tab=system");
    expect(await screen.findByRole("tab", { name: /^System/ })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tabpanel")).toBeInTheDocument();
    first.unmount();

    const old = open("/?tab=system");
    expect(await screen.findByRole("tab", { name: /^System/ })).toHaveAttribute("aria-selected", "true");
    old.unmount();

    open("/?tab=downloads");
    expect(await screen.findByRole("tab", { name: /^Live/ })).toHaveAttribute("aria-selected", "true");
  });

  it("carries no number on either tab, however many things need you: the bell holds the count (#1098)", async () => {
    open("/");
    await screen.findByRole("tab", { name: /^Live/ });
    await waitFor(() => expect(screen.getByRole("tab", { name: /^System/ }).textContent).toBe("System"));
    expect(screen.getByRole("tab", { name: /^Live/ }).textContent).toBe("Live");
    expect(screen.queryByRole("region", { name: "Needs you" })).toBeNull();
  });

  it("shows no badges on a new install", async () => {
    api.summary.mockReturnValue(emptySummary());
    open("/");
    await screen.findByRole("tab", { name: /^Live/ });
    for (const name of [/^Live/, /^System/]) {
      expect(within(screen.getByRole("tab", { name })).queryByLabelText(/need/)).toBeNull();
    }
  });
});

describe("dashboardShouldRevalidate", () => {
  const here = new URL("http://deluno.local/");

  it("does not re-run the loader for a same-address revalidation, such as returning to a hidden tab", () => {
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: here, defaultShouldRevalidate: true })).toBe(false);
  });

  it("does not re-run the loader when only the tab changes", () => {
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: new URL("http://deluno.local/?tab=system"), defaultShouldRevalidate: true })).toBe(false);
  });

  it("still revalidates after a form submission", () => {
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: here, formMethod: "POST", defaultShouldRevalidate: true })).toBe(true);
  });

  it("defers to React Router when the screen changes", () => {
    const next = new URL("http://deluno.local/movies");
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: next, defaultShouldRevalidate: true })).toBe(true);
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: next, defaultShouldRevalidate: false })).toBe(false);
  });
});

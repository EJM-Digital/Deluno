import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
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
  it("reads the tab from the address, and anything else is the Overview", () => {
    expect(tabFromSearch("?tab=library")).toBe("library");
    expect(tabFromSearch("?tab=downloads")).toBe("downloads");
    expect(tabFromSearch("?tab=system")).toBe("system");
    expect(tabFromSearch("")).toBe("overview");
    expect(tabFromSearch("?tab=nonsense")).toBe("overview");
  });
});

describe("DashboardPage tabs", () => {
  it("opens on the Overview and switches tab through the address, with no reload", async () => {
    const user = userEvent.setup();
    open("/");

    expect(await screen.findByRole("region", { name: /^needs you$/i })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /^Overview/ })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByLabelText("address")).toHaveTextContent("/");
    expect(screen.getByLabelText("address").textContent).toBe("/");

    await user.click(screen.getByRole("tab", { name: /^Library/ }));
    expect(screen.getByLabelText("address").textContent).toBe("/?tab=library");
    expect(screen.getByRole("tab", { name: /^Library/ })).toHaveAttribute("aria-selected", "true");
    expect(await screen.findByRole("region", { name: /^wanted$/i })).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: /^needs you$/i })).toBeNull();

    await user.click(screen.getByRole("tab", { name: /^System/ }));
    expect(screen.getByLabelText("address").textContent).toBe("/?tab=system");

    // Overview carries no tab at all.
    await user.click(screen.getByRole("tab", { name: /^Overview/ }));
    expect(screen.getByLabelText("address").textContent).toBe("/");
    // Switching tabs never asked for the summary again: it was already there.
    expect(api.fetchJson.mock.calls.filter(([path]) => path === "/api/dashboard/summary")).toHaveLength(1);
  });

  it("can be linked straight to a tab", async () => {
    open("/?tab=downloads");
    expect(await screen.findByRole("tab", { name: /^Downloads/ })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tabpanel")).toBeInTheDocument();
  });

  it("badges a tab with the number of things on it that need a look, and shows none at zero", async () => {
    open("/");
    const overview = await screen.findByRole("tab", { name: /^Overview/ });
    expect(within(overview).getByLabelText(/things need a look/)).toBeInTheDocument();
    const library = screen.getByRole("tab", { name: /^Library/ });
    expect(within(library).getByLabelText("1 thing needs a look")).toBeInTheDocument();
    // The other two tabs report their own problems; the stand-ins report none.
    expect(within(screen.getByRole("tab", { name: /^Downloads/ })).queryByLabelText(/need/)).toBeNull();
  });

  it("shows no badges on a new install", async () => {
    api.summary.mockReturnValue(emptySummary());
    open("/");
    await screen.findByRole("tab", { name: /^Overview/ });
    for (const name of [/^Overview/, /^Library/, /^Downloads/, /^System/]) {
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
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: new URL("http://deluno.local/?tab=library"), defaultShouldRevalidate: true })).toBe(false);
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

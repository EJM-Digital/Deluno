import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { queryClient } from "../lib/route-cache";
import { SystemPage } from "./system-screen";

// #1424: the real fetchJson and route cache run; only the HTTP layer answers, so a 500 from /api/backups is a real failed read.
const answers = vi.hoisted(() => ({ backups: "ok" as "ok" | "fail" }));
vi.mock("../lib/use-auth", () => ({
  authedFetch: async (path: string) => {
    const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
    if (path.startsWith("/api/backups/settings")) return json({ enabled: false, frequency: "Daily", timeOfDay: "03:00", retentionCount: 7, backupFolder: "/backups", nextRunUtc: null, problem: null });
    if (path.startsWith("/api/backups")) return answers.backups === "fail" ? new Response("boom", { status: 500 }) : json([]);
    if (path === "/api/settings") return json(emptyPlatformSettingsSnapshot);
    if (path.startsWith("/api/system/health")) return json({ checks: [], summary: { problemCount: 0 } });
    if (path.startsWith("/api/system/lan-access") || path.startsWith("/api/system/info") || path.startsWith("/api/monitoring")) return json(null);
    return json([]);
  }
}));

function renderScreen() {
  return render(<QueryClientProvider client={queryClient}><RouterProvider router={createMemoryRouter([{ path: "*", element: <SystemPage /> }], { initialEntries: ["/system/backups"] })} /></QueryClientProvider>);
}

beforeEach(() => { queryClient.clear(); answers.backups = "ok"; });

describe("System > Backups when GET /api/backups really fails (#1424)", () => {
  it("says it could not read the backups when the very first read gets a 500", async () => {
    answers.backups = "fail";
    renderScreen();
    expect((await screen.findAllByText(/Could not read the backups/, undefined, { timeout: 5000 })).length).toBeGreaterThan(0);
    expect(screen.queryByText("No backups yet")).toBeNull();
  });

  it("says it could not read the backups when an earlier read worked (empty) and the next one gets a 500", async () => {
    renderScreen();
    expect(await screen.findByText("No backups yet")).toBeInTheDocument();
    answers.backups = "fail";
    await queryClient.refetchQueries({ queryKey: ["src", "backups"], exact: true });
    expect((await screen.findAllByText(/Could not read the backups/, undefined, { timeout: 5000 })).length).toBeGreaterThan(0);
    expect(screen.queryByText("No backups yet")).toBeNull();
  }, 15_000);

  it("keeps the no-backups wording when the read worked and the list is empty", async () => {
    renderScreen();
    expect(await screen.findByText("No backups yet")).toBeInTheDocument();
    await waitFor(() => expect(queryClient.getQueryState(["src", "backups"])?.status).toBe("success"));
    expect(screen.queryByText(/Could not read the backups/)).toBeNull();
  });
});

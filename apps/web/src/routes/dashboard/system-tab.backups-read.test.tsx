import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../../lib/api";
import { queryClient } from "../../lib/route-cache";
import { fullSummary } from "./downloads-fixtures";
import { SystemTab } from "./system-tab";

// #1424: the real fetchJson runs; only the HTTP layer answers, so a 500 from /api/backups is a real failed read.
const answers = vi.hoisted(() => ({ backups: "ok" as "ok" | "fail" }));
vi.mock("../../lib/use-auth", () => ({
  authedFetch: async (path: string) => {
    const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
    if (path.startsWith("/api/backups/settings")) return json(null);
    if (path.startsWith("/api/backups")) return answers.backups === "fail" ? new Response("boom", { status: 500 }) : json([]);
    if (path === "/api/settings") return json(emptyPlatformSettingsSnapshot);
    if (path === "/api/setup/progress") return json({ lastCompletedStep: 9, isSkipped: false, isCompleted: true, updatedUtc: "2026-09-30T00:00:00Z" });
    return json([]);
  }
}));

function renderTab() {
  return render(<QueryClientProvider client={queryClient}><MemoryRouter><SystemTab summary={fullSummary} /></MemoryRouter></QueryClientProvider>);
}

beforeEach(() => { queryClient.clear(); answers.backups = "ok"; });

describe("Dashboard System tab when GET /api/backups really fails (#1424)", () => {
  it("says it could not read the backups when the very first read gets a 500", async () => {
    answers.backups = "fail";
    renderTab();
    expect(await screen.findByText(/Could not read the backups/, undefined, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.queryByText("No backup has been made yet.")).toBeNull();
  });

  it("says it could not read the backups when an earlier read worked (empty) and the next one gets a 500", async () => {
    renderTab();
    expect(await screen.findByText("No backup has been made yet.")).toBeInTheDocument();
    answers.backups = "fail";
    await queryClient.refetchQueries({ queryKey: ["src", "backups"], exact: true });
    expect(await screen.findByText(/Could not read the backups/, undefined, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.queryByText("No backup has been made yet.")).toBeNull();
  }, 15_000);

  it("keeps the no-backups wording when the read worked and the list is empty", async () => {
    renderTab();
    expect(await screen.findByText("No backup has been made yet.")).toBeInTheDocument();
    await waitFor(() => expect(queryClient.getQueryState(["src", "backups"])?.status).toBe("success"));
    expect(screen.queryByText(/Could not read the backups/)).toBeNull();
  });
});

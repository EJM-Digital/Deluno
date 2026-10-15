import { QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { fullSummary } from "./downloads-fixtures";
import { LiveTab } from "./live-tab";

const authedFetchMock = vi.fn();
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

let progressReads = 0;
let progress = { lastCompletedStep: 3, isSkipped: false, isCompleted: false, updatedUtc: "2026-10-15T00:00:00Z" };
vi.mock("../../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../../lib/api")>();
  const read = async (path: string) => {
    if (path === "/api/settings") return original.emptyPlatformSettingsSnapshot;
    if (path === "/api/setup/progress") { progressReads += 1; return progress; }
    return [];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

function renderTab() {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <LiveTab summary={fullSummary} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

beforeEach(() => {
  queryClient.clear();
  authedFetchMock.mockReset();
  authedFetchMock.mockResolvedValue({ ok: true, json: async () => ({}) });
  progressReads = 0;
  progress = { lastCompletedStep: 3, isSkipped: false, isCompleted: false, updatedUtc: "2026-10-15T00:00:00Z" };
});

describe("the Dashboard's guided-setup banner", () => {
  it("is offered while the guide is neither skipped nor completed, and the X hides it at once and writes isSkipped true keeping the step", async () => {
    renderTab();
    const banner = await screen.findByRole("region", { name: "Guided setup" });
    expect(banner).toBeInTheDocument();
    await waitFor(() => expect(progressReads).toBeGreaterThan(0));
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 50)); }); // reason: lets the answered read reach the screen before the X is pressed

    fireEvent.click(screen.getByRole("button", { name: "Dismiss onboarding" }));

    expect(screen.queryByRole("region", { name: "Guided setup" })).toBeNull();
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledTimes(1));
    const [url, init] = authedFetchMock.mock.calls[0];
    expect(url).toBe("/api/setup/progress");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body)).toEqual({ lastCompletedStep: 3, isSkipped: true, isCompleted: false });
  });

  it("is not shown when the guide was skipped", async () => {
    progress = { ...progress, isSkipped: true };
    renderTab();
    await screen.findByRole("region", { name: "Setup progress" });
    await waitFor(() => expect(screen.queryByRole("region", { name: "Guided setup" })).toBeNull());
  });

  it("is not shown once the guide was completed", async () => {
    progress = { ...progress, lastCompletedStep: 4, isCompleted: true };
    renderTab();
    await screen.findByRole("region", { name: "Setup progress" });
    await waitFor(() => expect(screen.queryByRole("region", { name: "Guided setup" })).toBeNull());
  });
});

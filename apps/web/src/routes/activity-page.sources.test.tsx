import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";

// #1329: Activity opens on its own shape until its first answer, and a source that cannot be read says so in place. The page and its
// sources are the real ones; only the network is stood in for.

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({ ...(await importOriginal<typeof import("../components/shell/toaster")>()), toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useLiveEvent: () => undefined,
  useSignalREvent: () => undefined
}));

const { ActivityPage } = await import("./activity-page");
const { queryClient } = await import("../lib/route-cache");

const page = (items: unknown[] = []) => ({ ok: true, status: 200, json: async () => ({ items, nextPageToken: null, hasMore: false }), text: async () => "" });
const recovery = () => ({ ok: true, status: 200, json: async () => ({ openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] }), text: async () => "" });
const refused = (status: number) => ({ ok: false, status, json: async () => ({}), text: async () => "" });

let answerFor: (path: string) => unknown = () => undefined;

beforeEach(() => {
  queryClient.clear();
  queryClient.setDefaultOptions({ queries: { retry: false } });
  window.localStorage.clear();
  authedFetchMock.mockReset();
  authedFetchMock.mockImplementation(async (path: string) => {
    const special = answerFor(path);
    if (special !== undefined) return special;
    if (path.includes("import-recovery")) return recovery();
    if (path.startsWith("/api/activity/filters")) return { ok: true, status: 200, json: async () => ({ types: [], sources: [], outcomes: [], reasons: [] }), text: async () => "" };
    return page();
  });
  answerFor = () => undefined;
});

function renderActivity() {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={["/activity"]}><ActivityPage /></MemoryRouter>
    </QueryClientProvider>
  );
}

describe("Activity's route sources (#1329)", () => {
  it("shows the page's own shape until the first answer, then the page", async () => {
    let release: (value: unknown) => void = () => undefined;
    answerFor = (path) => (path.startsWith("/api/jobs?pageSize=100") ? new Promise((resolve) => { release = resolve; }) : undefined);
    const view = renderActivity();

    expect(view.container.querySelector("[data-route-skeleton]")).not.toBeNull();
    expect(screen.queryByRole("radio", { name: "All" })).not.toBeInTheDocument();

    release(page());
    expect(await screen.findByRole("radio", { name: "All" })).toBeInTheDocument();
    expect(view.container.querySelector("[data-route-skeleton]")).toBeNull();
  });

  it("an optional source that cannot be read falls back to empty, says so in place with a Retry, and the page still draws", async () => {
    answerFor = (path) => (path.startsWith("/api/download-dispatches") ? refused(400) : undefined);
    renderActivity();

    expect(await screen.findByRole("radio", { name: "All" })).toBeInTheDocument();
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Sent downloads could not be read.");

    // The server answers on the next try: Retry reads it again and the notice goes.
    answerFor = () => undefined;
    const reads = authedFetchMock.mock.calls.filter((call) => String(call[0]).startsWith("/api/download-dispatches")).length;
    await userEvent.click(alert.querySelector("button")!);
    await waitFor(() => expect(authedFetchMock.mock.calls.filter((call) => String(call[0]).startsWith("/api/download-dispatches")).length).toBeGreaterThan(reads));
    await waitFor(() => expect(screen.queryByRole("alert")).not.toBeInTheDocument());
  });

  it("when a source the page cannot draw without fails, it says which one in place instead of the error page", async () => {
    answerFor = (path) => (path.startsWith("/api/jobs?pageSize=100") ? refused(400) : undefined);
    renderActivity();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("This screen could not load everything it needs.");
    expect(alert).toHaveTextContent("Jobs could not be read.");
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
  });
});

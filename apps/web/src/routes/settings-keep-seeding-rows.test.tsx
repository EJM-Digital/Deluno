import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { KeepSeedingView } from "../lib/api";

/**
 * Keep Seeding: what a client's category check reads as in each state the client can answer, and what Clear does with a move that
 * did not work. The answers are the ones the server's check and clear routes give; the page is the real one.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return { ...original, toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn(), dismiss: vi.fn() }) };
});

const revalidateMock = vi.fn();
let loaderData: KeepSeedingView;
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));
const { SettingsKeepSeedingPage } = await import("./settings-keep-seeding-page");

const view = (): KeepSeedingView => ({
  clients: [{ clientId: "q1", name: "My qBittorrent", protocol: "qbittorrent", canKeepSeeding: true, reason: null, category: null }],
  indexers: [],
  tagCategories: [],
  problems: [{
    key: "q1:abc", clientId: "q1", clientName: "My qBittorrent", itemId: "abc", releaseName: "Example.Movie.2026.1080p", kind: "refused", message: "Deluno could not move it.",
    category: "keep-seeding", firstSeenUtc: "2026-10-11T10:00:00Z", lastTriedUtc: "2026-10-11T10:10:00Z", attempts: 1
  }]
});
const reply = (body: unknown, status = 200) => Promise.resolve({ ok: status < 300, status, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)) } as Response);
const renderPage = () => render(<MemoryRouter><SettingsKeepSeedingPage /></MemoryRouter>);

beforeEach(() => { loaderData = view(); });
afterEach(() => { authedFetchMock.mockReset(); revalidateMock.mockReset(); toastSuccess.mockReset(); toastError.mockReset(); });

describe("the category check chip", () => {
  const states: Array<[string, string]> = [
    ["ready", "Ready"],
    ["missing", "Not in the client"],
    ["unreachable", "Could not check"],
    ["unsupported", "Not available"],
    ["configuration", "Needs a name"]
  ];
  for (const [status, words] of states) {
    it(`reads ${words} when the client answers ${status}, with the client's own message beside it`, async () => {
      authedFetchMock.mockImplementation(() => reply({ clientId: "q1", clientName: "My qBittorrent", category: "keep-seeding", status, message: `The client said ${status}.`, supported: status !== "unsupported", found: status === "ready" }));
      renderPage();
      fireEvent.click(screen.getAllByRole("row").find((row) => row.textContent?.includes("Not set"))!);
      fireEvent.change(await screen.findByLabelText("Category"), { target: { value: "keep-seeding" } });
      fireEvent.click(screen.getByRole("button", { name: "Check it" }));
      expect(await screen.findByText(words)).toBeInTheDocument();
      expect(screen.getByText(`The client said ${status}.`)).toBeInTheDocument();
      expect(screen.getByText(words).className).toContain(status === "ready" ? "text-success" : status === "missing" || status === "unreachable" ? "text-destructive" : "text-warning");
    });
  }
});

describe("Clear on a move that did not work", () => {
  it("posts to the problem's clear route, toasts, and reads the page again", async () => {
    authedFetchMock.mockImplementation(() => reply({ key: "q1:abc", cleared: true }));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Clear Example.Movie.2026.1080p" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Example.Movie.2026.1080p cleared"));
    expect(authedFetchMock).toHaveBeenCalledWith("/api/download-health/keep-seeding/problems/q1%3Aabc/clear", { method: "POST" });
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("counts a 404 as already cleared: the same toast and a fresh read", async () => {
    authedFetchMock.mockImplementation(() => reply({}, 404));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Clear Example.Movie.2026.1080p" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Example.Movie.2026.1080p cleared"));
    expect(toastError).not.toHaveBeenCalled();
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("any other failure toasts that it could not be cleared and does not read again", async () => {
    authedFetchMock.mockImplementation(() => Promise.resolve({ ok: false, status: 500, text: () => Promise.resolve(""), json: () => Promise.resolve(null) } as unknown as Response));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Clear Example.Movie.2026.1080p" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("That could not be cleared."));
    expect(toastSuccess).not.toHaveBeenCalled();
    expect(revalidateMock).not.toHaveBeenCalled();
  });
});

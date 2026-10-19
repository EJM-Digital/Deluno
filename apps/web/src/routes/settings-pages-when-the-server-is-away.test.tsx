import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * A settings page open while the server cannot be reached (the browser's own "Failed to fetch"): Save does not say it saved, keeps
 * what was typed, and can be pressed again once the server is back.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
const loaderData = { settings: {} as Record<string, unknown>, libraries: [], processorConnections: [] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");
const away = () => new TypeError("Failed to fetch");
const view = (path: string) => render(<MemoryRouter initialEntries={[path]}><SettingsMediaManagementPage /></MemoryRouter>);

beforeEach(() => {
  cleanup();
  loaderData.settings = { ...emptyPlatformSettingsSnapshot };
  vi.mocked(authedFetch).mockReset();
});

describe("a settings page while the server is away", () => {
  it("naming: Save shows an error, never Saved just now, keeps the edit, and works when the server is back", async () => {
    view("/settings/file-handling");
    fireEvent.click(screen.getByRole("switch", { name: /Replace illegal characters/ }));
    const before = screen.getByRole("switch", { name: /Replace illegal characters/ }).getAttribute("aria-checked");
    vi.mocked(authedFetch).mockRejectedValue(away());

    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(vi.mocked(authedFetch)).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByRole("button", { name: "Save changes" })).toBeEnabled());
    expect(screen.queryByText("Saved just now")).toBeNull();
    expect(screen.getByRole("switch", { name: /Replace illegal characters/ }).getAttribute("aria-checked")).toBe(before);

    vi.mocked(authedFetch).mockResolvedValue(new Response(JSON.stringify({ ...emptyPlatformSettingsSnapshot, replaceIllegalCharacters: before === "true" }), { status: 200 }));
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    expect(await screen.findByText("Saved just now")).toBeInTheDocument();
  });

  it("import policy: Save shows an error, never Saved just now, and keeps the edit", async () => {
    view("/settings/import-policy");
    fireEvent.click(screen.getByRole("switch", { name: /Keep seeding without a second copy/ }));
    const before = screen.getByRole("switch", { name: /Keep seeding without a second copy/ }).getAttribute("aria-checked");
    vi.mocked(authedFetch).mockRejectedValue(away());

    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));

    await waitFor(() => expect(vi.mocked(authedFetch)).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByRole("button", { name: "Save import settings" })).toBeEnabled());
    expect(screen.queryByText("Saved just now")).toBeNull();
    expect(screen.getByRole("switch", { name: /Keep seeding without a second copy/ }).getAttribute("aria-checked")).toBe(before);
  });

  it("says in plain words that the server could not be reached, not the browser's own 'Failed to fetch' (the footer shows the raw error text)", async () => {
    view("/settings/file-handling");
    fireEvent.click(screen.getByRole("switch", { name: /Replace illegal characters/ }));
    vi.mocked(authedFetch).mockRejectedValue(away());
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(vi.mocked(authedFetch)).toHaveBeenCalledTimes(1));
    expect(screen.queryByText("Failed to fetch")).toBeNull();
    expect(await screen.findByText(/could not reach|cannot reach|isn't answering|not answering/i)).toBeInTheDocument();
  });
});

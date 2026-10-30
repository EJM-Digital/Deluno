import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * Settings > Media management > Naming, saving (#1329): a refused save says what the server said in the page footer and keeps the
 * person's edit; an accepted one says Saved just now.
 */

vi.setConfig({ testTimeout: 30_000 });
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
const loaderData = { settings: {} as Record<string, unknown>, libraries: [], processorConnections: [] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");
const renderPage = () => render(<MemoryRouter initialEntries={["/settings/media-management"]}><SettingsMediaManagementPage /></MemoryRouter>);

beforeEach(() => {
  authedFetch.mockReset();
  loaderData.settings = { ...emptyPlatformSettingsSnapshot, movieFolderFormat: "{Movie Title} ({Release Year})", replaceIllegalCharacters: true, colonReplacementMode: "dash" };
});
afterEach(() => cleanup());

describe("saving the naming choices", () => {
  it("shows the server's own words in the footer when the save is refused, and sends only after a change", async () => {
    authedFetch.mockResolvedValue(new Response(JSON.stringify({ title: "Invalid", errors: { movieFolderFormat: ["The movie folder pattern needs a {Movie Title}."] } }), { status: 400, headers: { "Content-Type": "application/problem+json" } }));
    renderPage();
    expect(screen.getByRole("button", { name: "Save changes" })).toBeDisabled();

    fireEvent.click(screen.getByRole("switch", { name: /Replace illegal characters/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));

    expect(await screen.findByText("The movie folder pattern needs a {Movie Title}.")).toBeInTheDocument();
    expect(authedFetch).toHaveBeenCalledTimes(1);
    expect(authedFetch.mock.calls[0][0]).toBe("/api/settings");
    expect(authedFetch.mock.calls[0][1].method).toBe("PATCH");
    expect(JSON.parse(authedFetch.mock.calls[0][1].body)).toMatchObject({ replaceIllegalCharacters: false });
    expect(screen.getByRole("switch", { name: /Replace illegal characters/ })).not.toBeChecked();
  });

  it("says Saved just now when the server accepts it", async () => {
    authedFetch.mockImplementation(async (_path: string, init: RequestInit) => new Response(JSON.stringify({ ...(loaderData.settings), ...JSON.parse(String(init.body)) }), { status: 200, headers: { "Content-Type": "application/json" } }));
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: /Replace illegal characters/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
  });
});

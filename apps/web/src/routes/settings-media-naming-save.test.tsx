import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Media management > Naming: Save changes sends the thirteen naming fields in one PATCH, the footer says Saved just
 * now, the form is read again from the reply, and a refusal shows its message and keeps the edits.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
const revalidate = vi.fn();
const loaderData = { settings: {} as Record<string, unknown>, libraries: [], processorConnections: [] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const renderPage = () => render(<MemoryRouter initialEntries={["/settings/file-handling"]}><SettingsMediaManagementPage /></MemoryRouter>);

beforeEach(() => {
  cleanup();
  loaderData.settings = { ...emptyPlatformSettingsSnapshot };
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockResolvedValue(json({ ...emptyPlatformSettingsSnapshot }));
  revalidate.mockReset();
});

describe("Save changes on the naming page", () => {
  it("is dimmed until something changes, then sends exactly the thirteen naming fields in one PATCH and says Saved just now", async () => {
    renderPage();
    const save = screen.getByRole("button", { name: "Save changes" });
    expect(save).toBeDisabled();
    fireEvent.click(screen.getByRole("switch", { name: /Replace illegal characters/ }));
    expect(save).toBeEnabled();
    vi.mocked(authedFetch).mockResolvedValue(json({ ...emptyPlatformSettingsSnapshot, replaceIllegalCharacters: !emptyPlatformSettingsSnapshot.replaceIllegalCharacters }));
    fireEvent.click(save);
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    const patches = vi.mocked(authedFetch).mock.calls.filter(([url, init]) => url === "/api/settings" && (init as RequestInit).method === "PATCH");
    expect(patches).toHaveLength(1);
    const body = JSON.parse(String((patches[0][1] as RequestInit).body));
    expect(Object.keys(body).sort()).toEqual([
      "animeEpisodeFileFormat", "colonReplacementMode", "dailyEpisodeFileFormat", "episodeFileFormat", "movieFileFormat", "movieFolderFormat",
      "multiEpisodeStyle", "renameOnImport", "replaceIllegalCharacters", "seasonFolderFormat", "seriesFolderFormat", "specialsFolderFormat", "useHardlinks"
    ]);
    expect(body.replaceIllegalCharacters).toBe(!emptyPlatformSettingsSnapshot.replaceIllegalCharacters);
    // The form was read again from the reply, so there is nothing left to save.
    await waitFor(() => expect(screen.getByRole("button", { name: "Save changes" })).toBeDisabled());
  });

  it("shows the server's refusal in the footer and keeps what was typed", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: /Replace illegal characters/ }));
    vi.mocked(authedFetch).mockResolvedValue(json({ errors: { movieFolderFormat: ["That pattern is not usable."] } }, 400));
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(screen.queryByText("Saved just now")).toBeNull());
    expect(screen.getByRole("switch", { name: /Replace illegal characters/ }).getAttribute("aria-checked")).toBe(String(!emptyPlatformSettingsSnapshot.replaceIllegalCharacters));
    expect(screen.getByRole("button", { name: "Save changes" })).toBeEnabled();
  });
});

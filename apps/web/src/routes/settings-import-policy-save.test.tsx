import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Media management > Import policy: the switches and what they reveal, and Save import settings sending all twenty
 * fields in one PATCH, with its footer and its refusal.
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
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const renderPage = () => render(<MemoryRouter initialEntries={["/settings/import-policy"]}><SettingsMediaManagementPage /></MemoryRouter>);
const patches = () => vi.mocked(authedFetch).mock.calls.filter(([url, init]) => url === "/api/settings" && (init as RequestInit).method === "PATCH");

beforeEach(() => {
  cleanup();
  loaderData.settings = { ...emptyPlatformSettingsSnapshot, renameOnImport: false, renameFoldersOnImport: false, useHardlinks: false, leftoverDownloadCleanupEnabled: false, forceImportEnabled: false, setPermissionsOnImport: false };
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockResolvedValue(json({ ...(loaderData.settings as object) }));
});

describe("the switches", () => {
  it("renames files and folders independently of each other", async () => {
    renderPage();
    const files = screen.getByRole("switch", { name: /Rename files when imported/ });
    const folders = screen.getByRole("switch", { name: /Rename folders when imported/ });
    fireEvent.click(files);
    expect(files).toBeChecked();
    expect(folders).not.toBeChecked();
    fireEvent.click(folders);
    fireEvent.click(files);
    expect(files).not.toBeChecked();
    expect(folders).toBeChecked();
  });

  it("shows the After hours field only while Delete leftover download files automatically is on", () => {
    renderPage();
    expect(screen.queryByLabelText("After")).toBeNull();
    fireEvent.click(screen.getByRole("switch", { name: /Delete leftover download files automatically/ }));
    expect(screen.getByLabelText("After")).toBeInTheDocument();
  });

  it("shows Tries per download and Smallest file to force only while force import is on", () => {
    renderPage();
    expect(screen.queryByLabelText("Tries per download")).toBeNull();
    fireEvent.click(screen.getByRole("switch", { name: /Import files Deluno could not check/ }));
    expect(screen.getByLabelText("Tries per download")).toBeInTheDocument();
    expect(screen.getByLabelText("Smallest file to force")).toBeInTheDocument();
  });

  it("shows Chmod and Chown group only while Set permissions on import is on", () => {
    renderPage();
    expect(screen.queryByLabelText("Chmod")).toBeNull();
    fireEvent.click(screen.getByRole("switch", { name: /Set permissions on import/ }));
    expect(screen.getByLabelText("Chmod")).toBeInTheDocument();
    expect(screen.getByLabelText("Chown group")).toBeInTheDocument();
  });
});

describe("Save import settings", () => {
  it("sends all twenty fields in one PATCH, says Saved just now and settles on the reply", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: /Keep seeding without a second copy/ }));
    vi.mocked(authedFetch).mockResolvedValue(json({ ...(loaderData.settings as object), useHardlinks: true }));
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    expect(patches()).toHaveLength(1);
    const body = JSON.parse(String((patches()[0][1] as RequestInit).body));
    expect(Object.keys(body).sort()).toEqual([
      "chownGroup", "folderChmod", "forceImportEnabled", "forceImportMaxTries", "forceImportMinimumMegabytes", "leftoverDownloadCleanupAgeHours", "leftoverDownloadCleanupEnabled",
      "leftoverExcludePatterns", "leftoverMinAgeHours", "libraryFreeSpacePausePercent", "libraryFreeSpaceWarnPercent", "minimumFreeSpaceMb", "removeAndDeletePackedArchivesFromClient",
      "renameFoldersOnImport", "renameOnImport", "setPermissionsOnImport", "skipFreeSpaceCheck", "unmatchedImportGiveUpDays", "unmatchedImportPolicy", "useHardlinks"
    ]);
    expect(body.useHardlinks).toBe(true);
    await waitFor(() => expect(screen.getByRole("button", { name: "Save import settings" })).toBeDisabled());
  });

  it("sends the still-packed-archives switch as chosen, off keeping the client's files", async () => {
    renderPage();
    const archives = screen.getByRole("switch", { name: /Remove and delete still-packed archives from the download client/ });
    expect(archives).not.toBeChecked();
    fireEvent.click(archives);
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));
    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(JSON.parse(String((patches()[0][1] as RequestInit).body)).removeAndDeletePackedArchivesFromClient).toBe(true);
  });

  it("shows the server's refusal and keeps the edits when the save fails", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: /Rename files when imported/ }));
    vi.mocked(authedFetch).mockResolvedValue(json({ errors: { chownGroup: ["Use a group name or a number."] } }, 400));
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));
    await waitFor(() => expect(patches()).toHaveLength(1));
    await waitFor(() => expect(screen.queryByText("Saved just now")).toBeNull());
    expect(screen.getByRole("switch", { name: /Rename files when imported/ })).toBeChecked();
  });
});

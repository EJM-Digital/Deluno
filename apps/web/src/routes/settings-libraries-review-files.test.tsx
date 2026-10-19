import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Libraries: the Review existing files button in a saved library drawer opens the review dialog for that library, and is off
 * while the form has unsaved changes. The page scaffold is the same as in settings-libraries-page.test.tsx, with the review dialog
 * shown as a marker.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
vi.mock("./connections/folder-chain-section", () => ({ FolderChainSection: () => null }));
vi.mock("../components/app/existing-media-import-dialog", () => ({ ExistingMediaImportDialog: ({ open, library }: { open: boolean; library: { id: string; name: string } | null }) => (open && library ? <p>Review dialog for {library.name}</p> : null) }));
vi.mock("../components/ui/path-input", () => ({
  PathInput: ({ value, onChange, browseTitle }: { value: string; onChange: (value: string) => void; browseTitle?: string }) => (
    <input aria-label={browseTitle} value={value} onChange={(event) => onChange(event.target.value)} />
  )
}));
const toastSuccess = vi.hoisted(() => vi.fn());
const toastError = vi.hoisted(() => vi.fn());
vi.mock("../components/shell/toaster", () => ({ toast: { success: toastSuccess, error: toastError } }));
const revalidate = vi.fn();
const loaderData = { libraries: [] as unknown[], settings: {} as Record<string, unknown>, qualityProfiles: [] as unknown[] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsLibrariesPage } = await import("./settings-libraries-page");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const library = (over: Record<string, unknown> = {}) => ({
  id: "l1", name: "Films", mediaType: "movies", purpose: "Main library", rootPath: "D:\\Media\\Films", downloadsPath: null, qualityProfileId: "p1", qualityProfileName: "Standard",
  autoSearchEnabled: true, missingSearchEnabled: true, upgradeSearchEnabled: false, searchIntervalMinutes: 60, retryDelayHours: 6, maxItemsPerRun: 10,
  searchWindowStartHour: null, searchWindowEndHour: null, automationStatus: "idle", searchRequested: false, ...over
});
const profile = (id: string, name: string, mediaType = "movies") => ({ id, name, mediaType, cutoffQuality: "WEB 1080p" });
const renderPage = () => render(<MemoryRouter><SettingsLibrariesPage /></MemoryRouter>);
const calls = (method: string) => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

beforeEach(() => {
  cleanup();
  loaderData.libraries = [];
  loaderData.settings = { ...emptyPlatformSettingsSnapshot, movieRootPath: "D:\\Media\\Movies", seriesRootPath: "D:\\Media\\TV" };
  loaderData.qualityProfiles = [profile("p1", "Standard"), profile("p2", "Films 4K"), profile("p3", "Shows HD", "tv")];
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/libraries" ? json([library()]) : json({}));
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidate.mockReset();
});


describe("Review existing files in a saved library's drawer", () => {
  const openSaved = () => {
    loaderData.libraries = [library()];
    renderPage();
    fireEvent.click(screen.getByText("Films"));
  };

  it("opens the review dialog for that library", async () => {
    openSaved();
    expect(screen.queryByText("Review dialog for Films")).toBeNull();

    fireEvent.click(await screen.findByRole("button", { name: "Review existing files" }));

    expect(await screen.findByText("Review dialog for Films")).toBeInTheDocument();
  });

  it("is off, and says to save the library folder first, while the form has unsaved changes", async () => {
    openSaved();
    const button = await screen.findByRole("button", { name: "Review existing files" });
    expect(button).toBeEnabled();
    expect(screen.queryByText("Save the library folder before reviewing files.")).toBeNull();

    fireEvent.change(screen.getByPlaceholderText("Enter a library name"), { target: { value: "Films HD" } });

    expect(button).toBeDisabled();
    expect(screen.getByText("Save the library folder before reviewing files.")).toBeInTheDocument();
  });

  it("is not offered while a new library is being made", () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New library" })[0]);
    expect(screen.queryByRole("button", { name: "Review existing files" })).toBeNull();
  });
});

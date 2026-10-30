import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Libraries, live (#1329): the page asks to be read again when a library, a setting or a quality profile changes, and a
 * library renamed or removed elsewhere shows its new details in the drawer that is open on it.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const liveSpec = vi.hoisted(() => ({ current: null as null | { events?: readonly string[] } }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: (spec: { events?: readonly string[] }) => { liveSpec.current = spec; } }));
vi.mock("./connections/folder-chain-section", () => ({ FolderChainSection: () => null }));
vi.mock("../components/app/existing-media-import-dialog", () => ({ ExistingMediaImportDialog: () => null }));
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


beforeEach(() => {
  cleanup();
  loaderData.libraries = [];
  loaderData.settings = { ...emptyPlatformSettingsSnapshot, movieRootPath: "/media/movies", seriesRootPath: "/media/tv" };
  loaderData.qualityProfiles = [profile("p1", "Standard")];
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async () => json({}));
  liveSpec.current = null;
});

describe("the libraries page, live (#1329)", () => {
  it("asks to be read again on a library, settings or quality profile change", () => {
    renderPage();

    expect(liveSpec.current?.events).toEqual(["LibraryChanged", "SettingsChanged", "QualityProfileChanged"]);
  });

  it("a library moved elsewhere shows its new folder in the open drawer, and one removed elsewhere leaves the drawer on the name being edited", () => {
    loaderData.libraries = [library({ rootPath: "/media/films" })];
    const view = renderPage();
    fireEvent.click(screen.getByText("Films"));
    expect(screen.getByRole("dialog")).toHaveTextContent("Library · Movies · /media/films");

    loaderData.libraries = [library({ rootPath: "/media/new-films" })];
    view.rerender(<MemoryRouter><SettingsLibrariesPage /></MemoryRouter>);
    expect(screen.getByRole("dialog")).toHaveTextContent("Library · Movies · /media/new-films");

    loaderData.libraries = [];
    view.rerender(<MemoryRouter><SettingsLibrariesPage /></MemoryRouter>);
    const after = screen.getByRole("dialog");
    expect(after).not.toHaveTextContent("/media/new-films");
    expect(within(after).getByDisplayValue("Films")).toBeInTheDocument();
  });
});

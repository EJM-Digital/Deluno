import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Libraries: the list and its On/Off switch, the New library drawer (what it asks, what it refuses, what a created
 * library starts as), editing a library's folder and quality profile, a server refusal in the drawer's footer, and Remove.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
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


describe("the Libraries list (low rows)", () => {
  it("with no library it says 'No libraries yet' and offers a New library button", () => {
    renderPage();

    expect(screen.getByText("No libraries yet")).toBeVisible();
    expect(screen.getAllByRole("button", { name: "New library" }).length).toBeGreaterThan(0);
  });

  it("a row shows the name with Movies or TV shows, the folder, and where downloads come from", () => {
    loaderData.libraries = [
      library(),
      library({ id: "l2", name: "Shows", mediaType: "tv", rootPath: "D:/Media/TV", downloadsPath: "D:/Downloads/TV", qualityProfileId: "p3", qualityProfileName: "Shows HD" })
    ];
    renderPage();

    const films = screen.getByText("Films").closest("[role='row'], tr, li, div")!.parentElement as HTMLElement;
    expect(within(films).getByText("Movies")).toBeVisible();
    expect(within(films).getByText(String(library().rootPath))).toBeVisible();
    expect(within(films).getByText("Uses the download client's reported folder")).toBeVisible();
    expect(screen.getByText("TV shows")).toBeVisible();
    expect(screen.getByText("D:/Downloads/TV")).toBeVisible();
    expect(screen.getByText(/Advanced source:/)).toBeVisible();
  });

  it("the Library Profile cell reads the profile's name, a direct quality profile, or 'No profile assigned'", () => {
    loaderData.libraries = [
      library({ id: "a", name: "Via policy", defaultPolicySetName: "Family policy" }),
      library({ id: "b", name: "Direct" }),
      library({ id: "c", name: "Bare", qualityProfileId: null, qualityProfileName: null })
    ];
    renderPage();

    expect(screen.getByText("Family policy")).toBeVisible();
    expect(screen.getByText("Direct quality profile: Standard")).toBeVisible();
    expect(screen.getByText("No profile assigned")).toBeVisible();
    expect(screen.getByText("Stops at WEB 1080p")).toBeVisible();
  });

  it.each([
    [{ autoSearchEnabled: true, missingSearchEnabled: true }, "Scheduled"],
    [{ autoSearchEnabled: false, missingSearchEnabled: true }, "Paused"],
    [{ autoSearchEnabled: true, missingSearchEnabled: false, upgradeSearchEnabled: false }, "Not configured"],
    [{ autoSearchEnabled: true, missingSearchEnabled: true, searchRequested: true, automationStatus: "running" }, "Searching"]
  ])("a library with %j shows the status %s", (over, status) => {
    loaderData.libraries = [library(over)];
    renderPage();

    expect(screen.getByText(status)).toBeVisible();
  });

  it("a switch the server refuses toasts 'Could not pause searching for <name>.' and one for resuming says resume", async () => {
    loaderData.libraries = [library(), library({ id: "l2", name: "Paused one", autoSearchEnabled: false })];
    vi.mocked(authedFetch).mockResolvedValue(json({}, 400));
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Pause automatic searches for Films" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not pause searching for Films."));
    fireEvent.click(screen.getByRole("switch", { name: "Resume automatic searches for Paused one" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not resume searching for Paused one."));
  });
});

describe("the library drawer (low rows)", () => {
  it("choosing TV shows on create keeps the typed name, swaps to the TV root and drops a profile of the other kind", () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New library" })[0]);
    fireEvent.change(screen.getByPlaceholderText("Enter a library name"), { target: { value: "Kids" } });
    fireEvent.change(screen.getByLabelText("Quality profile"), { target: { value: "p2" } });

    fireEvent.click(within(screen.getByRole("radiogroup", { name: /Media type/ })).getByRole("radio", { name: "TV shows" }));

    expect(screen.getByPlaceholderText("Enter a library name")).toHaveValue("Kids");
    expect((screen.getByLabelText("Quality profile") as HTMLSelectElement).value).not.toBe("p2");
    const folder = screen.getAllByRole("textbox").find((box) => (box as HTMLInputElement).value === String(loaderData.settings.seriesRootPath));
    expect(folder).toBeTruthy();
  });

  it("a removal the server refuses toasts 'Library could not be removed.' and the library stays", async () => {
    loaderData.libraries = [library()];
    vi.mocked(authedFetch).mockResolvedValue(json({}, 400));
    renderPage();
    fireEvent.click(screen.getByText("Films"));

    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove library" }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Library could not be removed."));
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});

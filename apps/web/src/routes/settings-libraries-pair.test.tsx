import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Libraries, "Both" (#1411): the New library drawer makes a linked Movies library and TV library from one name and
 * one folder, shows the two libraries and their folders before saving, the list and the drawer name the partner, and Remove
 * asks whether to take both or only this one.
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
  loaderData.settings = { ...emptyPlatformSettingsSnapshot, movieRootPath: "/media/Movies", seriesRootPath: "/media/TV" };
  loaderData.qualityProfiles = [profile("p1", "Standard"), profile("p3", "Shows HD", "tv")];
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) =>
    init?.method === "POST" ? json([library({ id: "m", name: "Anime Movies" }), library({ id: "t", name: "Anime TV", mediaType: "tv" })]) : json([library()]));
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidate.mockReset();
});

const paired = () => [
  library({ id: "m", name: "Anime Movies", pairId: "x", pairedLibraryId: "t", pairedLibraryName: "Anime TV" }),
  library({ id: "t", name: "Anime TV", mediaType: "tv", pairId: "x", pairedLibraryId: "m", pairedLibraryName: "Anime Movies" })
];

describe("choosing Both when adding a library", () => {
  const openNew = () => fireEvent.click(screen.getAllByRole("button", { name: "New library" })[0]);

  it("shows the two libraries and their Movies and TV folders before anything is saved, and sends one pair request", async () => {
    renderPage();
    openNew();
    fireEvent.click(screen.getByRole("radio", { name: "Both" }));
    fireEvent.change(screen.getByPlaceholderText("Enter a library name"), { target: { value: "Anime" } });
    fireEvent.change(screen.getByLabelText("Choose movies and tv library folder"), { target: { value: "/media/anime" } });
    expect(screen.getByText("Anime Movies")).toBeInTheDocument();
    expect(screen.getByText("/media/anime/Movies")).toBeInTheDocument();
    expect(screen.getByText("Anime TV")).toBeInTheDocument();
    expect(screen.getByText("/media/anime/TV")).toBeInTheDocument();
    expect(calls("POST")).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "Create library" }));
    await waitFor(() => expect(calls("POST")).toHaveLength(1));
    expect(calls("POST")[0][0]).toBe("/api/libraries/pair");
    expect(bodyOf(calls("POST")[0])).toMatchObject({ name: "Anime", rootPath: "/media/anime", autoSearchEnabled: false });
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Anime Movies and Anime TV libraries created"));
  });

  it("is offered only when adding, never on a saved library", () => {
    loaderData.libraries = [library()];
    renderPage();
    fireEvent.click(screen.getByText("Films"));
    expect(screen.queryByRole("radio", { name: "Both" })).toBeNull();
  });

  it("refuses a blank name for the pair in plain words and sends nothing", async () => {
    renderPage();
    openNew();
    fireEvent.click(screen.getByRole("radio", { name: "Both" }));
    fireEvent.click(screen.getByRole("button", { name: "Create library" }));
    expect(await screen.findByText("Give this library a name.")).toBeInTheDocument();
    expect(calls("POST")).toHaveLength(0);
  });
});

describe("a pair in the list, the drawer and Remove", () => {
  it("names the partner in the list and in the drawer of either library", () => {
    loaderData.libraries = paired();
    renderPage();
    expect(screen.getByText(/Movies · Paired with Anime TV/)).toBeInTheDocument();
    expect(screen.getByText(/TV shows · Paired with Anime Movies/)).toBeInTheDocument();
    fireEvent.click(screen.getByText("Anime TV"));
    expect(screen.getAllByText(/Paired with Anime Movies/).length).toBeGreaterThan(1);
  });

  it("asks whether to remove both and removes both with ?pair=true", async () => {
    loaderData.libraries = paired();
    renderPage();
    fireEvent.click(screen.getByText("Anime Movies"));
    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove both libraries" }));
    await waitFor(() => expect(calls("DELETE")).toHaveLength(1));
    expect(calls("DELETE")[0][0]).toBe("/api/libraries/m?pair=true");
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Anime Movies and Anime TV removed"));
  });

  it("removes only this one when asked, leaving the partner", async () => {
    loaderData.libraries = paired();
    renderPage();
    fireEvent.click(screen.getByText("Anime Movies"));
    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove only this one" }));
    await waitFor(() => expect(calls("DELETE")).toHaveLength(1));
    expect(calls("DELETE")[0][0]).toBe("/api/libraries/m");
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Anime Movies removed"));
  });
});

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

describe("the list", () => {
  it("pauses and resumes automatic searches for one library by PUT of its schedule with only the switch changed, and toasts a refusal", async () => {
    loaderData.libraries = [library()];
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause automatic searches for Films" }));
    await waitFor(() => expect(calls("PUT")).toHaveLength(1));
    expect(calls("PUT")[0][0]).toBe("/api/libraries/l1/automation");
    expect(bodyOf(calls("PUT")[0])).toEqual({
      autoSearchEnabled: false, missingSearchEnabled: true, upgradeSearchEnabled: false, searchIntervalMinutes: 60, retryDelayHours: 6, maxItemsPerRun: 10, searchWindowStartHour: null, searchWindowEndHour: null
    });
    await waitFor(() => expect(revalidate).toHaveBeenCalled());

    vi.mocked(authedFetch).mockResolvedValue(json({ title: "No." }, 500));
    fireEvent.click(screen.getByRole("switch", { name: "Pause automatic searches for Films" }));
    await waitFor(() => expect(toastError).toHaveBeenCalled());
  });
});

describe("the New library drawer", () => {
  const openNew = () => fireEvent.click(screen.getAllByRole("button", { name: "New library" })[0]);

  it("refuses a blank name and a blank folder in plain words and sends nothing", async () => {
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, movieRootPath: "" };
    renderPage();
    openNew();
    fireEvent.click(screen.getByRole("button", { name: "Create library" }));
    expect(await screen.findByText("Give this library a name.")).toBeInTheDocument();
    expect(screen.getByText("Choose a folder for this library.")).toBeInTheDocument();
    expect(calls("POST")).toHaveLength(0);
  });

  it("creates an idle library: every search off, a 6-hour interval and a quality profile of the chosen name, or none for the standard one", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) =>
      String(input) === "/api/libraries" && init?.method === "POST" ? json(library({ id: "new", name: "Docs" }), 201) : String(input) === "/api/libraries" ? json([library({ id: "new", name: "Docs" })]) : json({}));
    renderPage();
    openNew();
    fireEvent.change(screen.getByPlaceholderText("Enter a library name"), { target: { value: "  Docs  " } });
    fireEvent.change(screen.getByLabelText("Choose movies library folder"), { target: { value: "D:\\Media\\Docs" } });
    fireEvent.click(screen.getByRole("button", { name: "Create library" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Docs library created"));
    expect(bodyOf(calls("POST")[0])).toMatchObject({
      name: "Docs", mediaType: "movies", rootPath: "D:\\Media\\Docs", downloadsPath: null, qualityProfileId: null,
      autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false, searchIntervalMinutes: 360, retryDelayHours: 6, maxItemsPerRun: 10
    });
    expect(revalidate).toHaveBeenCalled();
  });

  it("offers only the profiles of the chosen media type and sends the one picked", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) =>
      init?.method === "POST" ? json(library({ id: "new", name: "Shows" }), 201) : String(input) === "/api/libraries" ? json([library({ id: "new", name: "Shows", mediaType: "tv" })]) : json({}));
    renderPage();
    openNew();
    const profiles = () => [...(screen.getByLabelText("Quality profile") as HTMLSelectElement).options].map((option) => option.textContent);
    expect(profiles()).toEqual(["Standard (recommended)", "Standard", "Films 4K"]);
    fireEvent.click(screen.getByRole("radio", { name: "TV shows" }));
    expect(profiles()).toEqual(["Standard (recommended)", "Shows HD"]);
    fireEvent.change(screen.getByPlaceholderText("Enter a library name"), { target: { value: "Shows" } });
    fireEvent.change(screen.getByLabelText("Quality profile"), { target: { value: "p3" } });
    fireEvent.click(screen.getByRole("button", { name: "Create library" }));
    await waitFor(() => expect(calls("POST")).toHaveLength(1));
    expect(bodyOf(calls("POST")[0])).toMatchObject({ mediaType: "tv", qualityProfileId: "p3", rootPath: "D:\\Media\\TV" });
  });

  it("shows the server's words in the footer when it refuses the folder, and keeps the drawer open", async () => {
    vi.mocked(authedFetch).mockImplementation(async (_input: RequestInfo | URL, init?: RequestInit) =>
      init?.method === "POST" ? json({ errors: { rootPath: ["That folder does not exist."] } }, 400) : json({}));
    renderPage();
    openNew();
    fireEvent.change(screen.getByPlaceholderText("Enter a library name"), { target: { value: "Docs" } });
    fireEvent.click(screen.getByRole("button", { name: "Create library" }));
    expect(await screen.findByText(/That folder does not exist\./)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create library" })).toBeInTheDocument();
  });
});

describe("editing and removing a library", () => {
  const openSaved = () => {
    loaderData.libraries = [library()];
    renderPage();
    fireEvent.click(screen.getByText("Films"));
  };

  it("changing the quality profile PUTs to the library's quality-profile route and says Saved just now; a changed name or folder PUTs the details with the downloads folder always sent", async () => {
    openSaved();
    fireEvent.change(screen.getByLabelText("Quality profile"), { target: { value: "p2" } });
    fireEvent.change(screen.getByPlaceholderText("Enter a library name"), { target: { value: "Films HD" } });
    fireEvent.click(screen.getByRole("button", { name: "Save library" }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    const puts = calls("PUT").map(([url]) => url);
    expect(puts).toContain("/api/libraries/l1/quality-profile");
    expect(puts).toContain("/api/libraries/l1");
    expect(bodyOf(calls("PUT").find(([url]) => url === "/api/libraries/l1/quality-profile")!)).toEqual({ qualityProfileId: "p2" });
    expect(bodyOf(calls("PUT").find(([url]) => url === "/api/libraries/l1")!)).toEqual({ name: "Films HD", rootPath: "D:\\Media\\Films", downloadsPath: null });
  });

  it("locks the media type of a saved library", () => {
    openSaved();
    expect(within(screen.getByRole("radiogroup", { name: /Media type/ })).getByRole("radio", { name: "Movies" })).toBeDisabled();
  });

  it("removes after a confirmation that says the files stay on disk, toasts and reads again", async () => {
    openSaved();
    expect(screen.getByText(/Files stay on disk\./)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Remove/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove library" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Films removed"));
    expect(calls("DELETE")[0][0]).toBe("/api/libraries/l1");
    expect(revalidate).toHaveBeenCalled();
  });
});

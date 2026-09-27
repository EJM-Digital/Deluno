import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchJson, type LibraryItem, type QualityProfileItem } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { MovieEditDrawer } from "./movie-edit-drawer";

vi.mock("../../lib/api", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../lib/api")>(),
  fetchJson: vi.fn()
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: vi.fn() }));

const libraries: LibraryItem[] = [
  {
    id: "lib-main", name: "Main movies", mediaType: "movies", purpose: "primary", rootPath: "/library/movies-main",
    downloadsPath: null, qualityProfileId: null, qualityProfileName: null, cutoffQuality: null,
    upgradeUntilCutoff: false, upgradeUnknownItems: false, importWorkflow: "standard", processorName: null,
    processorOutputPath: null, processorTimeoutMinutes: 60, processorFailureMode: "block", autoSearchEnabled: false,
    missingSearchEnabled: false, upgradeSearchEnabled: false, searchIntervalHours: 6, retryDelayHours: 6,
    maxItemsPerRun: 10, searchWindowStartHour: null, searchWindowEndHour: null, automationStatus: "idle",
    searchRequested: false, lastSearchedUtc: null, nextSearchUtc: null, createdUtc: "2026-01-01T00:00:00Z",
    updatedUtc: "2026-01-01T00:00:00Z"
  },
  {
    id: "lib-4k", name: "4K movies", mediaType: "movies", purpose: "primary", rootPath: "/library/movies-4k",
    downloadsPath: null, qualityProfileId: null, qualityProfileName: null, cutoffQuality: null,
    upgradeUntilCutoff: false, upgradeUnknownItems: false, importWorkflow: "standard", processorName: null,
    processorOutputPath: null, processorTimeoutMinutes: 60, processorFailureMode: "block", autoSearchEnabled: false,
    missingSearchEnabled: false, upgradeSearchEnabled: false, searchIntervalHours: 6, retryDelayHours: 6,
    maxItemsPerRun: 10, searchWindowStartHour: null, searchWindowEndHour: null, automationStatus: "idle",
    searchRequested: false, lastSearchedUtc: null, nextSearchUtc: null, createdUtc: "2026-01-01T00:00:00Z",
    updatedUtc: "2026-01-01T00:00:00Z"
  }
];

const qualityProfiles: QualityProfileItem[] = [
  { id: "profile-1080p", name: "HD 1080p", mediaType: "movies" } as QualityProfileItem,
  { id: "profile-4k", name: "UHD 4K", mediaType: "movies" } as QualityProfileItem
];

function mockOptionLoads() {
  vi.mocked(fetchJson).mockImplementation(async (url: string) => {
    if (url === "/api/libraries") return libraries as never;
    if (url === "/api/quality-profiles") return qualityProfiles as never;
    if (url === "/api/tags") return [] as never;
    if (url.includes("/tags")) return [] as never;
    throw new Error(`unexpected fetchJson: ${url}`);
  });
}

function renderDrawer(onChanged = vi.fn()) {
  return render(
    <MovieEditDrawer
      open
      onOpenChange={vi.fn()}
      movieId="movie-1"
      monitored
      qualityProfileId={null}
      libraryId="lib-main"
      filePath="/library/movies-main/Dune Part Two (2024)/Dune.mkv"
      metadataJson={null}
      onChanged={onChanged}
    />
  );
}

describe("MovieEditDrawer (#784)", () => {
  beforeEach(() => {
    vi.mocked(fetchJson).mockReset();
    vi.mocked(authedFetch).mockReset();
    mockOptionLoads();
  });

  it("opens showing the current library and lets the tags editor be reached", async () => {
    renderDrawer();

    expect(await screen.findByRole("combobox", { name: "Path" })).toHaveValue("lib-main");
    expect(screen.getByText(/Current file:/)).toBeVisible();
    // Tags live in the same drawer, through the shared title tags editor.
    expect(screen.getByText("None assigned")).toBeVisible();
  });

  it("saves a quality profile change through the bulk quality-profile endpoint", async () => {
    const user = userEvent.setup();
    const onChanged = vi.fn();
    vi.mocked(authedFetch).mockResolvedValue(Response.json({ updated: 1 }));
    renderDrawer(onChanged);

    const select = await screen.findByRole("combobox", { name: "Quality profile" });
    await user.selectOptions(select, "profile-4k");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/bulk/quality-profile",
      expect.objectContaining({ body: JSON.stringify({ movieIds: ["movie-1"], qualityProfileId: "profile-4k" }) })
    ));
    expect(onChanged).toHaveBeenCalled();
  });

  it("requires a second click to confirm a path change, and never calls reassign-library on the first", async () => {
    const user = userEvent.setup();
    vi.mocked(authedFetch).mockResolvedValue(Response.json({ reassigned: 1, filesMoved: 0, moveFailures: [] }));
    renderDrawer();

    const pathSelect = await screen.findByRole("combobox", { name: "Path" });
    await user.selectOptions(pathSelect, "lib-4k");
    await user.click(screen.getByRole("button", { name: "Save" }));

    // First click only asks for confirmation - nothing was moved or reassigned yet.
    expect(authedFetch).not.toHaveBeenCalled();
    expect(await screen.findByRole("button", { name: "Confirm and save" })).toBeVisible();
    expect(screen.getByText(/Confirming will reassign the record to the new library/i)).toBeVisible();

    await user.click(screen.getByRole("button", { name: "Confirm and save" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/bulk/reassign-library",
      expect.objectContaining({
        body: JSON.stringify({ movieIds: ["movie-1"], fromLibraryId: "lib-main", toLibraryId: "lib-4k", moveFiles: false })
      })
    ));
  });

  it("passes moveFiles through once the mover checkbox is ticked before confirming", async () => {
    const user = userEvent.setup();
    vi.mocked(authedFetch).mockResolvedValue(Response.json({ reassigned: 1, filesMoved: 1, moveFailures: [] }));
    renderDrawer();

    const pathSelect = await screen.findByRole("combobox", { name: "Path" });
    await user.selectOptions(pathSelect, "lib-4k");
    await user.click(screen.getByRole("checkbox", { name: /move the file to the new root/i }));
    await user.click(screen.getByRole("button", { name: "Save" }));
    await user.click(screen.getByRole("button", { name: "Confirm and save" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/bulk/reassign-library",
      expect.objectContaining({
        body: JSON.stringify({ movieIds: ["movie-1"], fromLibraryId: "lib-main", toLibraryId: "lib-4k", moveFiles: true })
      })
    ));
  });
});

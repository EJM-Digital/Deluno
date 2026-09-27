import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * #909 added Settings → Import Policy → Storage safety: the library
 * free-space warn/pause thresholds the health-check schedule already reads
 * (LibraryRootHealthCheck) had no control anywhere in the UI to change them
 * from. This exercises the control saving both numbers, and refusing to save
 * a pause threshold set above the warn threshold rather than silently
 * clamping it after the fact.
 */
const mutate = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-api-mutation", () => ({
  useApiMutation: () => ({ mutate, state: "idle", fieldErrors: {} })
}));

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const settings = {
  ...emptyPlatformSettingsSnapshot,
  libraryFreeSpaceWarnPercent: 10,
  libraryFreeSpacePausePercent: 5,
  minimumFreeSpaceMb: 100,
  skipFreeSpaceCheck: false
};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLocation: () => ({ pathname: "/settings/import-policy" }),
  useLoaderData: () => ({ settings, libraries: [], qualityProfiles: [], processorConnections: [] })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");

describe("import policy storage safety thresholds (#909)", () => {
  afterEach(() => {
    mutate.mockReset();
  });

  it("shows the saved warn and pause percentages", () => {
    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    expect(screen.getByLabelText("Warn when a library has less than")).toHaveValue(10);
    expect(screen.getByLabelText("Pause imports when a library has less than")).toHaveValue(5);
  });

  it("saves both thresholds together", async () => {
    mutate.mockResolvedValueOnce({ ...settings, libraryFreeSpaceWarnPercent: 15, libraryFreeSpacePausePercent: 8 });

    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    fireEvent.change(screen.getByLabelText("Warn when a library has less than"), { target: { value: "15" } });
    fireEvent.change(screen.getByLabelText("Pause imports when a library has less than"), { target: { value: "8" } });
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));

    await vi.waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(mutate).toHaveBeenCalledWith(expect.objectContaining({
      libraryFreeSpaceWarnPercent: 15,
      libraryFreeSpacePausePercent: 8
    }));
  });

  it("refuses to save a pause threshold above the warn threshold", async () => {
    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    fireEvent.change(screen.getByLabelText("Warn when a library has less than"), { target: { value: "10" } });
    fireEvent.change(screen.getByLabelText("Pause imports when a library has less than"), { target: { value: "20" } });
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("The pause threshold must be at or below the warn threshold.");
    expect(mutate).not.toHaveBeenCalled();
  });
});

/**
 * #765: the import-time minimum free space guard (Radarr/Sonarr's 100MB
 * default floor) and its skip switch, added to the same Storage safety
 * section.
 */
describe("import-time minimum free space (#765)", () => {
  afterEach(() => {
    mutate.mockReset();
  });

  it("shows the saved minimum free space and skip switch", () => {
    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    expect(screen.getByLabelText("Minimum free space")).toHaveValue(100);
    expect(screen.getByRole("switch", { name: /^Skip free space check/ })).not.toBeChecked();
  });

  it("saves the minimum free space and skip switch together", async () => {
    mutate.mockResolvedValueOnce({ ...settings, minimumFreeSpaceMb: 500, skipFreeSpaceCheck: true });

    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    fireEvent.change(screen.getByLabelText("Minimum free space"), { target: { value: "500" } });
    fireEvent.click(screen.getByRole("switch", { name: /^Skip free space check/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));

    await vi.waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(mutate).toHaveBeenCalledWith(expect.objectContaining({
      minimumFreeSpaceMb: 500,
      skipFreeSpaceCheck: true
    }));
  });
});

/**
 * #887: whether a still-packed archive is also removed and deleted from the
 * download client, off by default -- blocklisting it stays a separate,
 * per-reason choice on the failure rules console.
 */
describe("still-packed archive cleanup (#887)", () => {
  afterEach(() => {
    mutate.mockReset();
  });

  it("shows the switch off by default", () => {
    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    expect(screen.getByRole("switch", { name: /^Remove and delete still-packed archives/ })).not.toBeChecked();
  });

  it("saves the switch when turned on", async () => {
    mutate.mockResolvedValueOnce({ ...settings, removeAndDeletePackedArchivesFromClient: true });

    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("switch", { name: /^Remove and delete still-packed archives/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));

    await vi.waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(mutate).toHaveBeenCalledWith(expect.objectContaining({
      removeAndDeletePackedArchivesFromClient: true
    }));
  });
});

import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptyContentBlocklistSettings, emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * Automation and Recovery, Failed downloads: the footer that saves the page's automation settings. It is closed while
 * nothing has changed, says Saving while the save is out, "Saved just now" when it lands, the server's reason when it does
 * not, and the page tells the shell there are unsaved changes while a choice is waiting to be saved.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const unsavedMock = vi.fn();
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: (dirty: boolean) => unsavedMock(dirty) }));

const loaderData = {
  automationStates: [],
  libraries: [],
  qualityModel: { version: "1", tiers: [], upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: false }, updatedUtc: "2026-10-01T00:00:00Z" },
  settings: emptyPlatformSettingsSnapshot,
  searchCycles: [],
  blockedReleases: [],
  failureRules: [],
  recycleBinSettings: { retentionDays: 30, maxSizeMb: 102400 },
  contentBlocklist: emptyContentBlocklistSettings
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn(), state: "idle" })
}));

const { SearchCyclesPage } = await import("./search-cycles-page");

function ok(body: unknown = {}) {
  return Promise.resolve({
    ok: true, status: 200, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body))
  } as unknown as Response);
}

function openFailedDownloads() {
  render(
    <MemoryRouter initialEntries={["/search-cycles/failed-downloads"]}>
      <SearchCyclesPage />
    </MemoryRouter>
  );
}

function makeAChange() {
  fireEvent.click(screen.getAllByRole("switch")[0]);
}

beforeEach(() => {
  unsavedMock.mockReset();
  authedFetchMock.mockReset();
  authedFetchMock.mockImplementation(() => ok([]));
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("the Save automation settings footer", () => {
  it("is closed and says nothing is waiting while nothing has changed", () => {
    openFailedDownloads();

    expect(screen.getByRole("button", { name: "Save automation settings" })).toBeDisabled();
    expect(screen.queryByText("Unsaved changes")).not.toBeInTheDocument();
    expect(unsavedMock).toHaveBeenLastCalledWith(false);
  });

  it("opens and says Unsaved changes the moment a choice is changed, and tells the shell to guard leaving", () => {
    openFailedDownloads();

    makeAChange();

    expect(screen.getByRole("button", { name: "Save automation settings" })).toBeEnabled();
    expect(screen.getByText("Unsaved changes")).toBeVisible();
    expect(unsavedMock).toHaveBeenLastCalledWith(true);
  });

  it("sends the settings, says Saved just now when they land, and closes again", async () => {
    openFailedDownloads();
    makeAChange();

    fireEvent.click(screen.getByRole("button", { name: "Save automation settings" }));

    await waitFor(() => expect(screen.getByText("Saved just now")).toBeVisible());
    const patch = authedFetchMock.mock.calls.find(([path, init]) => String(path) === "/api/settings" && (init as RequestInit | undefined)?.method === "PATCH");
    expect(patch).toBeDefined();
    expect(screen.getByRole("button", { name: "Save automation settings" })).toBeDisabled();
    expect(unsavedMock).toHaveBeenLastCalledWith(false);
  });

  it("says the server's reason when the save is refused and keeps the change waiting to be saved again", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => {
      if (String(path) === "/api/settings" && init?.method === "PATCH") {
        const problem = { title: "The strike limit must be between 1 and 20." };
        return Promise.resolve({
          ok: false, status: 400, json: () => Promise.resolve(problem), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(problem))
        } as unknown as Response);
      }
      return ok([]);
    });
    openFailedDownloads();
    makeAChange();

    fireEvent.click(screen.getByRole("button", { name: "Save automation settings" }));

    await waitFor(() => expect(screen.getByText("The strike limit must be between 1 and 20.")).toBeVisible());
    expect(screen.queryByText("Saved just now")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save automation settings" })).toBeEnabled();
    expect(unsavedMock).toHaveBeenLastCalledWith(true);
  });
});

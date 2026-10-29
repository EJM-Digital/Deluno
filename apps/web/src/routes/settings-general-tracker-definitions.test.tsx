import { render, screen, waitFor } from "@testing-library/react";
import { fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { LibraryItem, QualityProfileItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * #756: the scheduled tracker-definition update card on General settings --
 * off by default, shows when definitions last updated, and lets the owner
 * turn the schedule on/off and trigger an update now.
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const libraries: LibraryItem[] = [];
const qualityProfiles: QualityProfileItem[] = [];
const loaderData = { libraries, qualityProfiles, settings: emptyPlatformSettingsSnapshot };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const initialState = {
  isEnabled: false,
  intervalHours: 24,
  lastCheckedUtc: null,
  lastUpdatedUtc: null,
  status: "disabled",
  error: null,
  updatedTrackerIds: []
};

function mockFetchJson(overrides: Partial<typeof initialState> = {}) {
  const state = { ...initialState, ...overrides };
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("/api/trackers/definitions/update-check/run")) {
      return new Response(JSON.stringify({ ...state, isEnabled: state.isEnabled, status: "up-to-date", updatedTrackerIds: [] }), { status: 200 });
    }
    if (url.includes("/api/trackers/definitions/update-check")) {
      if (init?.method === "PUT") {
        const body = JSON.parse(init.body as string) as { isEnabled: boolean; intervalHours: number };
        return new Response(JSON.stringify({ ...state, isEnabled: body.isEnabled, intervalHours: body.intervalHours }), { status: 200 });
      }
      return new Response(JSON.stringify(state), { status: 200 });
    }
    if (url.includes("/api/system/lan-access")) {
      return new Response(JSON.stringify({ state: "notChecked", port: 5099, ruleName: null, message: "", canAllow: false }), { status: 200 });
    }
    if (url.includes("/api/system/log-level")) {
      return new Response(JSON.stringify({ level: "Information" }), { status: 200 });
    }
    return new Response(JSON.stringify({}), { status: 200 });
  });
}

const { SettingsGeneralPage } = await import("./settings-general-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsGeneralPage />
    </MemoryRouter>
  );
}

describe("scheduled tracker-definition update (#756)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("is off by default and shows definitions were never updated", async () => {
    mockFetchJson();
    renderPage();

    await waitFor(() => expect(screen.getByRole("switch", { name: "Update tracker definitions automatically" })).toBeTruthy());
    expect(screen.getByRole("switch", { name: "Update tracker definitions automatically" })).toHaveAttribute("aria-checked", "false");
    expect(screen.getByLabelText("Definitions last updated")).toHaveValue("Never");
  });

  it("turns the schedule on with a PUT", async () => {
    mockFetchJson();
    renderPage();

    await waitFor(() => expect(screen.getByRole("switch", { name: "Update tracker definitions automatically" })).toBeTruthy());
    fireEvent.click(screen.getByRole("switch", { name: "Update tracker definitions automatically" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/trackers/definitions/update-check",
      expect.objectContaining({ method: "PUT" })
    ));
    const putCall = vi.mocked(authedFetch).mock.calls.find(([url, init]) =>
      (url as string).includes("update-check") && (init as RequestInit | undefined)?.method === "PUT");
    expect(JSON.parse((putCall![1] as RequestInit).body as string)).toEqual({ isEnabled: true, intervalHours: 24 });
  });

  it("triggers an immediate update with the Update now button", async () => {
    mockFetchJson();
    renderPage();

    await waitFor(() => expect(screen.getByRole("button", { name: "Update now" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Update now" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/trackers/definitions/update-check/run",
      expect.objectContaining({ method: "POST" })
    ));
  });
});

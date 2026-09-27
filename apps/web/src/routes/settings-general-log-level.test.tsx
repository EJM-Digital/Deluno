import { render, screen, waitFor } from "@testing-library/react";
import { fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { LibraryItem, QualityProfileItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * #866: a runtime-configurable Log Level in Settings, changeable while
 * Deluno is running. Covers the new LogLevelCard on the General settings
 * page reading /api/system/log-level and PUTting a change.
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

function mockFetchJson(level: string) {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("/api/system/log-level")) {
      return new Response(JSON.stringify({ level }), { status: 200, headers: { "Content-Type": "application/json" } });
    }
    if (url.includes("/api/system/lan-access")) {
      return new Response(JSON.stringify({ state: "notChecked", port: 5099, ruleName: null, message: "", canAllow: false }), { status: 200 });
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

describe("runtime log level (#866)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows the current effective level read from /api/system/log-level", async () => {
    mockFetchJson("Information");
    renderPage();

    await waitFor(() => expect(screen.getByRole("combobox", { name: "Log level" })).toBeTruthy());
    expect((screen.getByRole("combobox", { name: "Log level" }) as HTMLSelectElement).value).toBe("Information");
  });

  it("PUTs the new level immediately when changed", async () => {
    mockFetchJson("Information");
    renderPage();

    await waitFor(() => expect(screen.getByRole("combobox", { name: "Log level" })).toBeTruthy());
    fireEvent.change(screen.getByRole("combobox", { name: "Log level" }), { target: { value: "Debug" } });

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/system/log-level",
      expect.objectContaining({ method: "PUT" })
    ));
    const putCall = vi.mocked(authedFetch).mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === "PUT");
    expect(JSON.parse((putCall![1] as RequestInit).body as string)).toEqual({ level: "Debug" });
  });
});

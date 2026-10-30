import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyContentBlocklistSettings, emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * Automation and Recovery, Failed downloads, Content blocklist: the optional "Custom remote list URL". A blank address is
 * saved as no address at all (null), never as an empty string, and an address is saved trimmed. The server side (a refresh
 * that fails keeps the last good patterns) is proven in ContentBlocklistRemoteRefreshServiceTests.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

let blocklist = { ...emptyContentBlocklistSettings };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({
    automationStates: [],
    libraries: [],
    qualityModel: { version: "1", tiers: [], upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: false }, updatedUtc: "2026-10-01T00:00:00Z" },
    settings: emptyPlatformSettingsSnapshot,
    searchCycles: [],
    blockedReleases: [],
    failureRules: [],
    recycleBinSettings: { retentionDays: 30, maxSizeMb: 102400 },
    contentBlocklist: blocklist
  }),
  useRevalidator: () => ({ revalidate: vi.fn(), state: "idle" })
}));

const { SearchCyclesPage } = await import("./search-cycles-page");

function ok(body: unknown = {}) {
  return Promise.resolve({
    ok: true, status: 200, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body))
  } as unknown as Response);
}

function open() {
  render(
    <MemoryRouter initialEntries={["/search-cycles/failed-downloads"]}>
      <SearchCyclesPage />
    </MemoryRouter>
  );
}

function sentRemoteUrl(): unknown {
  const put = authedFetchMock.mock.calls.find(([path, init]) => String(path) === "/api/download-health/content-blocklist" && (init as RequestInit | undefined)?.method === "PUT");
  expect(put).toBeDefined();
  return JSON.parse(String((put![1] as RequestInit).body)).remoteUrl;
}

beforeEach(() => {
  authedFetchMock.mockReset();
  authedFetchMock.mockImplementation(() => ok([]));
  blocklist = { ...emptyContentBlocklistSettings };
});

describe("Custom remote list URL", () => {
  it("is saved trimmed when an address is typed", async () => {
    open();

    fireEvent.change(screen.getByLabelText("Custom remote list URL"), { target: { value: "  https://example.com/blocklist.txt  " } });
    fireEvent.click(screen.getByRole("button", { name: "Save automation settings" }));

    await waitFor(() => expect(screen.getByText("Saved just now")).toBeVisible());
    expect(sentRemoteUrl()).toBe("https://example.com/blocklist.txt");
  });

  it("is saved as no address at all when an address that was set is cleared", async () => {
    blocklist = { ...emptyContentBlocklistSettings, remoteUrl: "https://example.com/blocklist.txt" };
    open();
    const field = screen.getByLabelText("Custom remote list URL");
    expect(field).toHaveValue("https://example.com/blocklist.txt");

    fireEvent.change(field, { target: { value: "   " } });
    fireEvent.click(screen.getByRole("button", { name: "Save automation settings" }));

    await waitFor(() => expect(screen.getByText("Saved just now")).toBeVisible());
    expect(sentRemoteUrl()).toBeNull();
  });
});

import { act, render, screen, waitFor } from "@testing-library/react";
import { createRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CreateFormDraft } from "../../hooks/use-library-create";

/**
 * The paused notice follows the server and nothing asks on a timer (#936, #1179): a pause or a resume made in another tab arrives as a
 * SettingsChanged push, a reconnect reads the setting again, and the Add dialog draws the notice only while "Search now" is ticked.
 */

const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  settingsChanged: [] as Array<() => void>,
  resync: [] as Array<() => void>
}));

vi.mock("../../lib/api", () => ({ fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock("../../lib/use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard" },
  useSignalREvent: (event: string, _subject: unknown, handler: () => void) => {
    if (event === "SettingsChanged") mocks.settingsChanged.push(handler);
  },
  useSignalRResync: (handler: () => void) => { mocks.resync.push(handler); }
}));

const { AutomationPausedNotice } = await import("./automation-paused-notice");
const { LibraryCreateDialog } = await import("./library-create-dialog");

const pausedSetting = (paused: boolean) => mocks.fetchJson.mockImplementation(async (url: string) => url === "/api/settings" ? { autoStartJobs: !paused } : []);
const latest = (list: Array<() => void>) => list.at(-1)!;

beforeEach(() => {
  mocks.fetchJson.mockReset();
  mocks.settingsChanged.length = 0;
  mocks.resync.length = 0;
});
afterEach(() => vi.useRealTimers());

describe("the paused notice follows the server", () => {
  it("appears when another tab pauses automation and goes when another tab resumes it, with no timer", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    pausedSetting(false);
    const { container } = render(<AutomationPausedNotice context="dashboard" />);
    await waitFor(() => expect(mocks.fetchJson).toHaveBeenCalledTimes(1));
    expect(container.textContent).toBe("");

    pausedSetting(true);
    await act(async () => { latest(mocks.settingsChanged)(); });
    expect(await screen.findByText(/Automation is paused, so nothing is searched/)).toBeVisible();

    pausedSetting(false);
    await act(async () => { latest(mocks.settingsChanged)(); });
    await waitFor(() => expect(container.textContent).toBe(""));

    const reads = mocks.fetchJson.mock.calls.length;
    await act(async () => { vi.advanceTimersByTime(10 * 60 * 1000); });
    expect(mocks.fetchJson.mock.calls.length, "nothing is read on a timer").toBe(reads);
  });

  it("reads the setting again after a reconnect, so a change missed while offline still shows", async () => {
    pausedSetting(false);
    const { container } = render(<AutomationPausedNotice context="dashboard" />);
    await waitFor(() => expect(mocks.fetchJson).toHaveBeenCalledTimes(1));
    expect(container.textContent).toBe("");

    pausedSetting(true);
    await act(async () => { latest(mocks.resync)(); });

    expect(await screen.findByRole("status")).toHaveTextContent(/Automation is paused/);
  });

  it("keeps what was last known when the setting cannot be read", async () => {
    pausedSetting(true);
    render(<AutomationPausedNotice context="add" noun="show" />);
    expect(await screen.findByText("Automation is paused, so this show won't be searched until you resume it.")).toBeVisible();

    mocks.fetchJson.mockRejectedValue(new Error("offline"));
    await act(async () => { latest(mocks.settingsChanged)(); });

    expect(screen.getByText("Automation is paused, so this show won't be searched until you resume it.")).toBeVisible();
  });
});

describe("the Add dialog draws the notice only while Search now is ticked", () => {
  function renderDialog(searchNow: boolean) {
    render(
      <LibraryCreateDialog
        open onOpenChange={vi.fn()} variant="movies" label="movies" singular="movie" metadataStatus={null} isCreating={false}
        createForm={{ title: "arrival", year: "", imdbId: "", monitored: true, monitorPreset: "all", searchNow, qualityProfileId: "", tags: "", metadata: null } as CreateFormDraft}
        setCreateForm={vi.fn()} metadataResults={[]} setMetadataResults={vi.fn()} selectedMetadataResults={[]} setSelectedMetadataResults={vi.fn()}
        isSearchingMetadata={false} metadataSearchSequence={createRef<number>() as never} onSearch={vi.fn()} onSelectResult={vi.fn()}
        onOpenHeldResult={vi.fn()} onCreate={vi.fn()}
      />
    );
  }

  it("says the film will not be searched while paused and Search now is ticked", async () => {
    pausedSetting(true);
    renderDialog(true);
    expect(await screen.findByText("Automation is paused, so this film won't be searched until you resume it.")).toBeVisible();
  });

  it("says nothing when Search now is not ticked, even while paused", async () => {
    pausedSetting(true);
    renderDialog(false);
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(screen.queryByText(/Automation is paused/)).not.toBeInTheDocument();
    expect(mocks.fetchJson).not.toHaveBeenCalledWith("/api/settings");
  });
});

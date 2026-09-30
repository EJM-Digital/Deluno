import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { SystemPage } from "./system-screen";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: toastMock, Toaster: () => null }));

const revalidateMock = vi.fn();
const loaderData = {
  activity: [],
  activityDisplaySettings: { pageSize: 250 },
  automation: [],
  backupSettings: null,
  backups: [],
  downloadClients: [],
  healthChecks: { checks: [], summary: { problemCount: 0 } },
  indexers: [],
  jobs: [],
  lanAccess: null,
  libraries: [],
  monitoring: null,
  processors: [],
  recycleBin: [],
  recycleBinSettings: null,
  retryWindows: [],
  searchCycles: [],
  settings: emptyPlatformSettingsSnapshot,
  systemInfo: null,
  systemTasks: []
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" }),
  useNavigate: () => vi.fn()
}));

function jsonResponse(body: unknown, ok = true) {
  return Promise.resolve({
    ok,
    status: ok ? 200 : 500,
    json: () => Promise.resolve(body)
  } as Response);
}

/**
 * #871: the Activity timeline's page-size control actually renders, and
 * changing it saves the new value and re-fetches with it -- not just that
 * the source text mentions the right identifiers.
 */
describe("Activity timeline page size (#871)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
  });

  it("renders the page-size card showing the loaded setting", () => {
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "POST" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system/audit"]}>
        <SystemPage />
      </MemoryRouter>
    );

    const select = screen.getByLabelText("Rows shown") as HTMLSelectElement;
    expect(select.value).toBe("250");
  });

  it("changing the select saves the new page size and refetches the timeline", async () => {
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "POST" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system/audit"]}>
        <SystemPage />
      </MemoryRouter>
    );

    const select = screen.getByLabelText("Rows shown") as HTMLSelectElement;
    fireEvent.change(select, { target: { value: "1000" } });

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/activity/display-settings",
        expect.objectContaining({ method: "PUT", body: JSON.stringify({ pageSize: 1000 }) })
      )
    );

    // The select now reflects the saved value, and the loader is asked to
    // re-run -- which is what actually re-fetches Activity at the new size.
    await waitFor(() => expect(select.value).toBe("1000"));
    expect(revalidateMock).toHaveBeenCalled();
  });
});

/** #1073: a one-time note on the Health screen is cleared by hand, from the screen. */
describe("Health notes are clearable (#1073)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
    loaderData.healthChecks = { checks: [], summary: { problemCount: 0 } };
    loaderData.monitoring = null;
  });

  it("a notice row has a Clear button that deletes it and refreshes; an ordinary row does not", async () => {
    loaderData.healthChecks = {
      checks: [
        { id: "notice:download-folders", category: "notice", name: "Downloads now go to the library's downloads folder", status: "problem", message: "Moved.", cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null },
        { id: "indexer:1", category: "indexer", name: "An indexer", status: "problem", message: "Down.", cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null }
      ],
      summary: { problemCount: 2 }
    } as never;
    loaderData.monitoring = { alerts: [] } as never;
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "DELETE" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );

    expect(screen.getAllByRole("button", { name: /^Clear:/ })).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Clear: Downloads now go to the library's downloads folder" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/system/health/notices/notice%3Adownload-folders",
        expect.objectContaining({ method: "DELETE" })
      )
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });
});

describe("The unreachable-library note can confirm the files are gone (#1070)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
    toastMock.success.mockReset();
    toastMock.error.mockReset();
    loaderData.healthChecks = { checks: [], summary: { problemCount: 0 } };
    loaderData.monitoring = null;
  });

  // Only the files-gone route gets the answer; every other request the screen makes gets an empty list.
  function answerFilesGone(body: unknown, ok = true) {
    authedFetchMock.mockImplementation((url: string) => (url.includes("files-gone") ? jsonResponse(body, ok) : jsonResponse([])));
  }

  function renderUnreachableNote() {
    loaderData.healthChecks = {
      checks: [
        { id: "notice:library-unreachable:lib-1", category: "notice", name: "Movies 4K looks unreachable", status: "problem", message: "m", cause: null, fix: null, detailsJson: JSON.stringify({ libraryId: "lib-1", missing: 30, total: 40, raisedUtc: "2026-09-30T00:00:00Z" }), lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null }
      ],
      summary: { problemCount: 1 }
    } as never;
    loaderData.monitoring = { alerts: [] } as never;
    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );
  }

  async function confirmFilesGone() {
    fireEvent.click(screen.getByRole("button", { name: "These files really are gone: Movies 4K looks unreachable" }));
    expect(await screen.findByText(/Nothing is deleted from your disk/)).toBeTruthy();
    expect(screen.getByText(/about 30 files/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Mark them as gone" }));
  }

  it("asks first, and shows the success message from the result", async () => {
    answerFilesGone({ markedGone: 29, cameBack: 1, couldNotChange: 0, libraryUnreachable: false, message: "Movies 4K: 29 files marked as gone, 1 came back and was left alone." });
    renderUnreachableNote();

    fireEvent.click(screen.getByRole("button", { name: "These files really are gone: Movies 4K looks unreachable" }));
    expect(authedFetchMock).not.toHaveBeenCalledWith(expect.stringContaining("files-gone"), expect.anything());
    fireEvent.click(await screen.findByRole("button", { name: "Mark them as gone" }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Movies 4K: 29 files marked as gone, 1 came back and was left alone."));
    expect(toastMock.error).not.toHaveBeenCalled();
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("shows the reason when Deluno refuses because the drive is still unreachable", async () => {
    answerFilesGone({ markedGone: 0, cameBack: 0, couldNotChange: 0, libraryUnreachable: true, message: "Movies 4K is still not reachable, so Deluno changed nothing." });
    renderUnreachableNote();

    await confirmFilesGone();

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Movies 4K is still not reachable, so Deluno changed nothing."));
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  it("shows the reason when there is nothing left to act on", async () => {
    answerFilesGone({ markedGone: 0, cameBack: 30, couldNotChange: 0, libraryUnreachable: false, message: "Every file Deluno tracks in Movies 4K is there now, so there is nothing to mark as gone." });
    renderUnreachableNote();

    await confirmFilesGone();

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(expect.stringContaining("nothing to mark as gone")));
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  it("says a large answer is running in the background", async () => {
    answerFilesGone({ markedGone: 0, cameBack: 0, couldNotChange: 0, libraryUnreachable: false, inBackground: true, message: "Deluno is marking about 30 files as gone in the background." });
    renderUnreachableNote();

    await confirmFilesGone();

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Deluno is marking about 30 files as gone in the background."));
  });

  it("shows an error toast when the request fails or throws", async () => {
    answerFilesGone({}, false);
    renderUnreachableNote();
    await confirmFilesGone();
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledTimes(1));

    authedFetchMock.mockImplementation((url: string) => (url.includes("files-gone") ? Promise.reject(new Error("offline")) : jsonResponse([])));
    await confirmFilesGone();
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledTimes(2));
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  it("offers These files really are gone next to Clear and posts to the library's route", async () => {
    loaderData.healthChecks = {
      checks: [
        { id: "notice:library-unreachable:lib-1", category: "notice", name: "Movies 4K looks unreachable", status: "problem", message: "Movies 4K: 30 of 40 files could not be found.", cause: null, fix: null, detailsJson: JSON.stringify({ libraryId: "lib-1", missing: 2, total: 5 }), lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null },
        { id: "notice:download-folders", category: "notice", name: "Other note", status: "problem", message: "Moved.", cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null }
      ],
      summary: { problemCount: 2 }
    } as never;
    loaderData.monitoring = { alerts: [] } as never;
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "POST" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );

    expect(screen.getAllByRole("button", { name: /^These files really are gone/ })).toHaveLength(1);
    expect(screen.getAllByRole("button", { name: /^Clear:/ })).toHaveLength(2);
    fireEvent.click(screen.getByRole("button", { name: "These files really are gone: Movies 4K looks unreachable" }));
    fireEvent.click(await screen.findByRole("button", { name: "Mark them as gone" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/filesystem/libraries/lib-1/files-gone",
        expect.objectContaining({ method: "POST" })
      )
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });
});

describe("A start-up step that did not finish can be run again or cleared (#1080)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
    loaderData.healthChecks = { checks: [], summary: { problemCount: 0 } };
    loaderData.monitoring = null;
  });

  const row = (id: string, name: string) => ({ id, category: "system", name, status: "problem", message: "Did not finish.", cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null });

  it("Try again re-runs the step and Clear removes the row; a running job's row only snoozes", async () => {
    loaderData.healthChecks = {
      checks: [
        row("system:background-problem:startup:service-restart-on-failure", "Deluno may not restart itself"),
        row("system:background-problem:startup:collection-exclusions", "Collection exclusions were not moved"),
        row("system:background-problem:lane:import", "Background jobs: import lane keeps failing")
      ],
      summary: { problemCount: 3 }
    } as never;
    loaderData.monitoring = { alerts: [] } as never;
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) =>
      jsonResponse(init?.method === "POST" ? { cleared: true, message: "It worked, so the note is gone." } : init?.method === "DELETE" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );

    expect(screen.getAllByRole("button", { name: /^Try again:/ })).toHaveLength(2);
    expect(screen.getAllByRole("button", { name: /^Clear:/ })).toHaveLength(2);
    expect(screen.getAllByRole("button", { name: "Snooze 24h" })).toHaveLength(1);

    fireEvent.click(screen.getByRole("button", { name: "Try again: Deluno may not restart itself" }));
    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/system/health/background-problems/startup%3Aservice-restart-on-failure/retry",
        expect.objectContaining({ method: "POST" })
      )
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());

    fireEvent.click(screen.getByRole("button", { name: "Clear: Collection exclusions were not moved" }));
    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/system/health/background-problems/startup%3Acollection-exclusions",
        expect.objectContaining({ method: "DELETE" })
      )
    );
  });
});

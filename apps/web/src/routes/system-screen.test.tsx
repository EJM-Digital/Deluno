import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { SystemPage } from "./system-screen";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

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
    authedFetchMock.mockImplementation(() => jsonResponse({}));

    render(
      <MemoryRouter initialEntries={["/system/audit"]}>
        <SystemPage />
      </MemoryRouter>
    );

    const select = screen.getByLabelText("Rows shown") as HTMLSelectElement;
    expect(select.value).toBe("250");
  });

  it("changing the select saves the new page size and refetches the timeline", async () => {
    authedFetchMock.mockImplementation(() => jsonResponse({}));

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

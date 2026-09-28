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

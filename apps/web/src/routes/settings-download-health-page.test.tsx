import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ActivityEventItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * #964: the "Connectivity check" card on Settings -> Download health reads
 * its skip history straight from the same Activity entries the remediation
 * pass writes (DownloadClientTelemetryService.RunConfiguredHealthRemediationAsync,
 * category "download.health.connectivity-check-skipped"), rather than
 * keeping a counter of its own.
 */

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const settings = {
  ...emptyPlatformSettingsSnapshot,
  downloadHealthConnectivityCheckEnabled: true,
  downloadHealthConnectivityCheckUrls: "https://1.1.1.1"
};

const skip: ActivityEventItem = {
  id: "evt-skip-1",
  category: "download.health.connectivity-check-skipped",
  message: "Connectivity check found no reachable URL; this pass was skipped and nothing was struck.",
  detailsJson: null,
  relatedJobId: null,
  relatedEntityType: "download_client",
  relatedEntityId: "connectivity-check",
  createdUtc: new Date(Date.now() - 20 * 60 * 1000).toISOString()
};

let loaderData = {
  rules: [],
  ignoreEntries: [],
  settings,
  connectivityCheckSkips: [skip]
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsDownloadHealthPage } = await import("./settings-download-health-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsDownloadHealthPage />
    </MemoryRouter>
  );
}

describe("connectivity check skip history (#964)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows when the check last skipped a pass, how many times recently, and links to Activity", () => {
    renderPage();

    expect(screen.getByText(/Last skipped a pass/i)).toBeInTheDocument();
    expect(screen.getByText(/1 time in the last 24 hours/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /See in Activity/i })).toHaveAttribute("href", "/activity?status=done");
  });

  it("says it has never skipped a pass, and shows no link, when history is empty", () => {
    loaderData = { ...loaderData, connectivityCheckSkips: [] };
    renderPage();

    expect(screen.getByText(/Has not skipped a pass/i)).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /See in Activity/i })).not.toBeInTheDocument();
  });
});

describe("strike counts and check interval are settable on the screen (#1194)", () => {
  it("shows the shared Check every setting and the stalled-download strike count, with the person's numbers", () => {
    loaderData = { ...loaderData, connectivityCheckSkips: [] };
    renderPage();

    expect(screen.getByLabelText("Check every")).toHaveValue(5);
    expect(screen.getByLabelText("Strikes before a stalled download is acted on")).toHaveValue(3);
    expect(screen.getByText(/Each check where a problem is found adds one strike, and the first check where it is gone clears them/i)).toBeInTheDocument();
  });
});

import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OnboardingBanner } from "./onboarding-banner";
import { SetupProgressLadder } from "./setup-progress-ladder";
import { buildSetupStatus } from "../../lib/setup-status";
import type { PlatformSettingsSnapshot } from "../../lib/api";

afterEach(cleanup);

const fresh = () => buildSetupStatus({
  libraries: [], downloadClients: [], indexers: [], intakeSources: [], policySets: [], qualityProfiles: [],
  settings: { autoStartJobs: false, movieFolderFormat: "{Movie Title}", seriesFolderFormat: "{Series Title}", episodeFileFormat: "{Episode Title}" } as PlatformSettingsSnapshot
});

function renderLadder() {
  return render(
    <MemoryRouter initialEntries={["/"]}>
      <Routes>
        <Route path="/" element={<SetupProgressLadder status={fresh()} />} />
        <Route path="*" element={<p>Arrived somewhere else</p>} />
      </Routes>
    </MemoryRouter>
  );
}

describe("the setup progress ladder tiles", () => {
  it("shows the four required steps as numbered tiles, each with its state and its action, and leaves the optional one off", () => {
    renderLadder();

    const tiles = screen.getAllByRole("link");
    expect(tiles.map((tile) => tile.getAttribute("aria-label"))).toEqual([
      "1. Media Management: Not configured",
      "2. Library Profiles: No quality decision yet",
      "3. Find & Download: Search sources and download clients still need to be connected",
      "4. Automation & Recovery: Background automation is paused"
    ]);
    expect(screen.queryByText(/Discover Media/)).toBeNull();
    expect(screen.getByText("Configure media management")).toBeTruthy();
    expect(screen.getByText("Choose a quality profile")).toBeTruthy();
    expect(screen.getByText("0/4 steps complete")).toBeTruthy();
  });

  it("points each tile at its step's screen", () => {
    renderLadder();

    const hrefs = screen.getAllByRole("link").map((tile) => tile.getAttribute("href"));
    expect(hrefs).toEqual(["/settings/libraries", "/settings/profiles", "/indexers", "/search-cycles"]);
  });

  it("pressing a tile opens that step's screen", () => {
    renderLadder();

    fireEvent.click(screen.getByRole("link", { name: /^3\. Find & Download/ }));

    expect(screen.getByText("Arrived somewhere else")).toBeTruthy();
  });

  it("marks the first unfinished step as the current one and a failed connection as needing attention", () => {
    const model = buildSetupStatus({
      libraries: [{ mediaType: "movies", rootPath: "D:\\Movies", qualityProfileId: "q" }] as never,
      downloadClients: [], indexers: [{ isEnabled: true, healthStatus: "failed" }] as never, intakeSources: [], policySets: [], qualityProfiles: [{}] as never,
      settings: { autoStartJobs: false, movieFolderFormat: "{Movie Title}", seriesFolderFormat: "{S}", episodeFileFormat: "{E}" } as PlatformSettingsSnapshot
    });
    render(<MemoryRouter><SetupProgressLadder status={model} /></MemoryRouter>);

    expect(screen.getByRole("link", { name: /^3\. Find & Download: Needs attention/ })).toBeTruthy();
    expect(screen.getByRole("link", { name: /^1\. Media Management: / }).className).toContain("border-success");
  });
});

describe("the onboarding banner", () => {
  it("shows the guided setup prompt, and Dismiss hides it and calls the handler exactly once", () => {
    const onDismiss = vi.fn();
    render(<MemoryRouter><OnboardingBanner isSetupSuppressed={false} onDismiss={onDismiss} /></MemoryRouter>);

    expect(screen.getByRole("region", { name: "Guided setup" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Dismiss onboarding" }));

    expect(onDismiss).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("region", { name: "Guided setup" })).toBeNull();
  });

  it("shows nothing once the guide was completed or dismissed before", () => {
    const { container } = render(<MemoryRouter><OnboardingBanner isSetupSuppressed onDismiss={vi.fn()} /></MemoryRouter>);

    expect(container).toBeEmptyDOMElement();
  });
});

import { render, screen, within } from "@testing-library/react";
import { fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { GuidePackage, QualityModelSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));

/**
 * The plain choices behind a quality profile's score guardrails and preferred
 * language (#778, #769), and the folded "Show the numbers" section that keeps
 * the TRaSH custom-format scores out of the way for everyone else (#938).
 */

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  compileQualityProfilePreferences: vi.fn()
}));

const qualityModel: QualityModelSnapshot = {
  version: "test",
  tiers: [
    { name: "WEB 720p", rank: 1, movieMinGb: 1, movieMaxGb: 4, episodeMinMb: 100, episodeMaxMb: 800, scoreCeiling: 0 },
    { name: "WEB 1080p", rank: 2, movieMinGb: 2, movieMaxGb: 8, episodeMinMb: 200, episodeMaxMb: 1600, scoreCeiling: 0 }
  ],
  upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: true },
  updatedUtc: new Date(0).toISOString()
};

const guide: GuidePackage = {
  id: "guide",
  name: "Test guide",
  version: 1,
  schemaVersion: 1,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "",
  qualityTiers: [
    { id: "web-720p", label: "WEB 720p", source: "web", resolution: "720p", minMbPerMin: 1, maxMbPerMin: 4, rank: 1 },
    { id: "web-1080p", label: "WEB 1080p", source: "web", resolution: "1080p", minMbPerMin: 2, maxMbPerMin: 8, rank: 2 }
  ],
  customFormats: [],
  // #778: TRaSH's own score guardrails for this pinned profile - the starter
  // picker's mapping into a saved profile is expected to carry these three
  // numbers through exactly, not the generic defaults.
  qualityProfiles: [
    {
      id: "balanced-movies",
      name: "Balanced",
      tagline: "",
      description: "A balanced starting point.",
      highlights: [],
      mediaType: "movies",
      qualityOrder: ["web-1080p", "web-720p"],
      cutoffQualityId: "web-1080p",
      upgradeAllowed: true,
      minFormatScore: 1500,
      cutoffFormatScore: 6000,
      minUpgradeFormatScore: 100,
      recommendedFormats: []
    }
  ],
  bundles: []
};

const loaderData = {
  libraries: [],
  qualityProfiles: [],
  customFormats: [],
  settings: {},
  qualityModel,
  guide
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsProfilesPage } = await import("./settings-profiles-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsProfilesPage />
    </MemoryRouter>
  );
}

describe("quality profile plain choices (#938)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("opens the drawer with the plain cutoff, upgrade-gain, never-grab-bad and language choices, each with its own accessible name", async () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New quality profile" })[0]);

    expect(await screen.findByRole("dialog")).toBeTruthy();

    expect(screen.getByRole("combobox", { name: "Stop upgrading once a copy is" })).toBeTruthy();
    expect(screen.getByRole("combobox", { name: "Only replace a file when the new copy is" })).toBeTruthy();
    expect(screen.getByRole("switch", { name: /^Never grab copies TRaSH rates as bad/ })).toBeTruthy();
    expect(screen.getByRole("combobox", { name: "Preferred language" })).toBeTruthy();
    expect(screen.getByRole("switch", { name: /^Require this language/ })).toBeTruthy();
  });

  it("keeps the numbers folded until Show the numbers is opened, and gives each number field a unique name", async () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New quality profile" })[0]);
    await screen.findByRole("dialog");

    // Folded: the number fields are not in the accessibility tree at all.
    expect(screen.queryByRole("spinbutton", { name: "Minimum custom-format score to grab" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /Show the numbers/ }));

    expect(screen.getByRole("spinbutton", { name: "Minimum custom-format score to grab" })).toBeTruthy();
    expect(screen.getByRole("spinbutton", { name: "Cutoff custom-format score to stop upgrading" })).toBeTruthy();
    expect(screen.getByRole("spinbutton", { name: "Minimum custom-format score gain required for an upgrade" })).toBeTruthy();
  });

  it("defaults the cutoff choice to the best available and the upgrade-gain choice to a little better", async () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New quality profile" })[0]);
    await screen.findByRole("dialog");

    const cutoff = screen.getByRole("combobox", { name: "Stop upgrading once a copy is" }) as HTMLSelectElement;
    const gain = screen.getByRole("combobox", { name: "Only replace a file when the new copy is" }) as HTMLSelectElement;

    expect(within(cutoff).getByRole("option", { selected: true }).textContent).toBe("The best available");
    expect(within(gain).getByRole("option", { selected: true }).textContent).toBe("A little better");
  });

  it("disables Require this language until a language is chosen", async () => {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New quality profile" })[0]);
    await screen.findByRole("dialog");

    const requireLanguage = screen.getByRole("switch", { name: /^Require this language/ }) as HTMLButtonElement;
    expect(requireLanguage.disabled).toBe(true);

    fireEvent.change(screen.getByRole("combobox", { name: "Preferred language" }), { target: { value: "en" } });

    expect(requireLanguage.disabled).toBe(false);
  });
});

/**
 * #778: TRaSH's pinned profile definitions already carry minFormatScore,
 * cutoffFormatScore and minUpgradeFormatScore, but nothing server-side turns a
 * pinned guide profile into a saved QualityProfile - the web starter picker
 * (applyStarter, settings-profiles-page.tsx) building a CreateQualityProfileRequest
 * client-side and POSTing it to POST /api/quality-profiles is the only route
 * from guide data to a persisted profile. This exercises that mapping end to
 * end: choosing the starter through to the exact numbers the saved request
 * carries.
 */
describe("TRaSH starter picker carries the guide's score guardrails into the saved profile (#778)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("sends the pinned profile's own minFormatScore, cutoffFormatScore and minUpgradeFormatScore, not the generic defaults", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, json: async () => ({ id: "new" }) } as Response);

    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New quality profile" })[0]);
    await screen.findByRole("dialog");

    fireEvent.change(screen.getByRole("combobox", { name: "Choose a template" }), { target: { value: "balanced-movies" } });
    fireEvent.click(screen.getByRole("button", { name: "Create quality profile" }));

    await vi.waitFor(() => expect(authedFetch).toHaveBeenCalled());

    const [, init] = vi.mocked(authedFetch).mock.calls[0];
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.scoreLimits).toEqual({
      minFormatScore: 1500,
      cutoffFormatScore: 6000,
      minUpgradeFormatScore: 100
    });
  });
});

import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import type { LibraryItem } from "../lib/api/types/resources";

/**
 * #936: automatic subtitle fetching has one global switch now, in Settings ->
 * Subtitles (this screen), rather than only being turned on implicitly by a
 * library asking for a language. This covers the switch reflecting the saved
 * value and saving a change to it -- the same PATCH /api/settings shape every
 * other settings toggle in Deluno uses.
 */

const revalidate = vi.fn();
const loaderData = {
  libraries: [],
  languages: [],
  settings: { ...emptyPlatformSettingsSnapshot, subtitleFetchAutomaticEnabled: false }
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SubtitleLanguagesPage } = await import("./subtitle-languages-screen");

function renderPage() {
  return render(
    <MemoryRouter>
      <SubtitleLanguagesPage />
    </MemoryRouter>
  );
}

describe("Settings -> Subtitles: fetch automatically switch (#936)", () => {
  afterEach(() => {
    revalidate.mockReset();
    vi.unstubAllGlobals();
  });

  it("reflects the saved value", () => {
    renderPage();
    expect(screen.getByRole("switch", { name: "Fetch subtitles automatically" })).not.toBeChecked();
  });

  it("saves a settings PATCH turning the switch on", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Fetch subtitles automatically" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/settings");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(init.body as string)).toEqual({ subtitleFetchAutomaticEnabled: true });
    expect(revalidate).toHaveBeenCalled();
  });
});

/** #836: a per-language hearing-impaired preference, saved to /api/libraries/{id}/subtitles. */
describe("Subtitle languages: per-language hearing-impaired preference (#836)", () => {
  const library: LibraryItem = {
    id: "lib-1",
    name: "Movies",
    mediaType: "movies",
    purpose: "Main",
    rootPath: "D:\\Media",
    downloadsPath: null,
    qualityProfileId: null,
    qualityProfileName: null,
    cutoffQuality: null,
    upgradeUntilCutoff: false,
    upgradeUnknownItems: false,
    importWorkflow: "standard",
    processorName: null,
    processorOutputPath: null,
    processorTimeoutMinutes: 60,
    processorFailureMode: "block",
    autoSearchEnabled: true,
    missingSearchEnabled: true,
    upgradeSearchEnabled: true,
    searchIntervalHours: 6,
    retryDelayHours: 24,
    maxItemsPerRun: 25,
    searchWindowStartHour: null,
    searchWindowEndHour: null,
    automationStatus: "idle",
    searchRequested: false,
    lastSearchedUtc: null,
    nextSearchUtc: null,
    createdUtc: "2026-01-01T00:00:00Z",
    updatedUtc: "2026-01-01T00:00:00Z",
    subtitleLanguages: ["en"],
    subtitleLanguageMode: "all"
  };

  const libraryLoaderData = {
    libraries: [library],
    languages: [{ code: "en", name: "English" }],
    settings: emptyPlatformSettingsSnapshot
  };

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("defaults to plain preferred and saves a changed choice", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
    vi.stubGlobal("fetch", fetchMock);
    Object.assign(loaderData, libraryLoaderData);

    render(
      <MemoryRouter>
        <SubtitleLanguagesPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("row", { name: /Movies/ }));
    const select = await screen.findByRole("combobox", { name: "Hearing-impaired subtitles for English" });
    expect(select).toHaveTextContent("Plain preferred");

    fireEvent.click(select);
    fireEvent.click(await screen.findByRole("option", { name: /Plain only/ }));

    fireEvent.click(screen.getByRole("button", { name: /Save/ }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const call = fetchMock.mock.calls.find(([url]) => (url as string).includes("/subtitles"));
    expect(call).toBeDefined();
    const body = JSON.parse((call![1] as RequestInit).body as string);
    expect(body.languageTypePreferences).toEqual([{ language: "en", hearingImpaired: "plain-only", includeForced: false }]);
  });

  it("saves the forced-subtitle toggle for a language (#835)", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
    vi.stubGlobal("fetch", fetchMock);
    Object.assign(loaderData, libraryLoaderData);

    render(
      <MemoryRouter>
        <SubtitleLanguagesPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("row", { name: /Movies/ }));
    fireEvent.click(await screen.findByRole("switch", { name: "Also fetch forced English subtitles" }));
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const call = fetchMock.mock.calls.find(([url]) => (url as string).includes("/subtitles"));
    const body = JSON.parse((call![1] as RequestInit).body as string);
    expect(body.languageTypePreferences).toEqual([{ language: "en", hearingImpaired: "plain-preferred", includeForced: true }]);
  });
});

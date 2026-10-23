import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, headers: new Headers(), json: async () => ({}) });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Fetch subtitles automatically" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/settings");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(init.body as string)).toEqual({ subtitleFetchAutomaticEnabled: true });
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
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
    searchIntervalMinutes: 6,
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
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, headers: new Headers(), json: async () => ({}) });
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
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, headers: new Headers(), json: async () => ({}) });
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

  it("names the configured chmod when permissions are on (#838)", async () => {
    Object.assign(loaderData, {
      ...libraryLoaderData,
      settings: { ...emptyPlatformSettingsSnapshot, setPermissionsOnImport: true, folderChmod: "755" }
    });

    render(
      <MemoryRouter>
        <SubtitleLanguagesPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("row", { name: /Movies/ }));
    expect(screen.getByText((_, element) =>
      element?.tagName === "P" && /Set permissions on written subtitles.*chmod \(755\)/.test(element.textContent ?? "")
    )).toBeInTheDocument();
  });
});

/**
 * #838: the chmod/chown setting Import Policy already has for imported media
 * is also a control in the Subtitles settings area itself, not only a status
 * line pointing elsewhere — because the issue's own words are "in the
 * Subtitles settings area". It PATCHes the same /api/settings fields Import
 * Policy's own Permissions section writes, so there is one value, not two
 * that could disagree.
 */
describe("Subtitle languages: permission control lives in this screen too (#838)", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("reflects the saved switch and chmod", () => {
    Object.assign(loaderData, {
      libraries: [],
      languages: [],
      settings: { ...emptyPlatformSettingsSnapshot, setPermissionsOnImport: true, folderChmod: "750", chownGroup: "media" }
    });

    render(
      <MemoryRouter>
        <SubtitleLanguagesPage />
      </MemoryRouter>
    );

    expect(screen.getByRole("switch", { name: /^Set permissions on written subtitles/ })).toBeChecked();
    expect(screen.getByLabelText("Chmod")).toHaveValue("750");
    expect(screen.getByLabelText("Chown group")).toHaveValue("media");
  });

  it("saves a settings PATCH turning the switch on", async () => {
    Object.assign(loaderData, {
      libraries: [],
      languages: [],
      settings: { ...emptyPlatformSettingsSnapshot, setPermissionsOnImport: false }
    });
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, headers: new Headers(), json: async () => ({}) });
    vi.stubGlobal("fetch", fetchMock);

    render(
      <MemoryRouter>
        <SubtitleLanguagesPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("switch", { name: /^Set permissions on written subtitles/ }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/settings");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(init.body as string)).toEqual({ setPermissionsOnImport: true, folderChmod: "", chownGroup: "" });
  });

  it("saves a typed chmod value on blur-sm", async () => {
    Object.assign(loaderData, {
      libraries: [],
      languages: [],
      settings: { ...emptyPlatformSettingsSnapshot, setPermissionsOnImport: true, folderChmod: "" }
    });
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, headers: new Headers(), json: async () => ({}) });
    vi.stubGlobal("fetch", fetchMock);

    render(
      <MemoryRouter>
        <SubtitleLanguagesPage />
      </MemoryRouter>
    );

    const chmod = screen.getByLabelText("Chmod");
    fireEvent.change(chmod, { target: { value: "755" } });
    fireEvent.blur(chmod);

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/settings");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(init.body as string)).toEqual({ setPermissionsOnImport: true, folderChmod: "755", chownGroup: "" });
  });

  it("keeps both fields when chmod and then chown group are edited back to back (#1375)", async () => {
    Object.assign(loaderData, {
      libraries: [],
      languages: [],
      settings: { ...emptyPlatformSettingsSnapshot, setPermissionsOnImport: true, folderChmod: "", chownGroup: "" }
    });
    // The first save is still in flight when the second edit is made.
    let finishFirst: (value: unknown) => void = () => {};
    const reply = { ok: true, status: 200, headers: new Headers(), json: async () => ({}) };
    const fetchMock = vi.fn()
      .mockImplementationOnce(() => new Promise((resolve) => { finishFirst = resolve; }))
      .mockResolvedValue(reply);
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    const user = userEvent.setup();
    await user.type(screen.getByLabelText("Chmod"), "755");
    await user.tab();
    // The chown field must still take typing while the first save is out.
    await user.type(screen.getByLabelText("Chown group"), "media");
    await user.tab();
    finishFirst(reply);

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    const bodies = fetchMock.mock.calls.map(([, init]) => JSON.parse((init as RequestInit).body as string));
    expect(bodies[0]).toEqual({ setPermissionsOnImport: true, folderChmod: "755", chownGroup: "" });
    expect(bodies[1]).toEqual({ setPermissionsOnImport: true, folderChmod: "755", chownGroup: "media" });
    expect(screen.getByLabelText("Chmod")).toHaveValue("755");
    expect(screen.getByLabelText("Chown group")).toHaveValue("media");
  });
});

/**
 * #988: a switch so an embedded picture-only subtitle track (PGS/VobSub)
 * does not count as already holding the language, so a text subtitle is
 * still fetched. Off by default -- the same default "count subtitles inside
 * the video" already uses -- so every library keeps exactly what Deluno did
 * before this setting existed.
 */
describe("Subtitle languages: picture-based embedded subtitles do not count (#988)", () => {
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
    searchIntervalMinutes: 6,
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

  it("reflects today's behaviour (on) for a library that has never opened this setting", async () => {
    Object.assign(loaderData, libraryLoaderData);

    render(
      <MemoryRouter>
        <SubtitleLanguagesPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("row", { name: /Movies/ }));
    const dontCount = await screen.findByRole("switch", { name: "Picture-based subtitles (PGS/VobSub) don't count" });
    expect(dontCount).not.toBeChecked();
  });

  it("saves pictureBasedCounts: false when turned on", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, headers: new Headers(), json: async () => ({}) });
    vi.stubGlobal("fetch", fetchMock);
    Object.assign(loaderData, libraryLoaderData);

    render(
      <MemoryRouter>
        <SubtitleLanguagesPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("row", { name: /Movies/ }));
    fireEvent.click(await screen.findByRole("switch", { name: "Picture-based subtitles (PGS/VobSub) don't count" }));
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const call = fetchMock.mock.calls.find(([url]) => (url as string).includes("/subtitles"));
    expect(call).toBeDefined();
    const body = JSON.parse((call![1] as RequestInit).body as string);
    expect(body.pictureBasedCounts).toBe(false);
    expect(body.embeddedCounts).toBe(true);
  });

  it("names the setting in the list when it is off", () => {
    Object.assign(loaderData, {
      ...libraryLoaderData,
      libraries: [{ ...library, subtitlePictureBasedCounts: false }]
    });

    render(
      <MemoryRouter>
        <SubtitleLanguagesPage />
      </MemoryRouter>
    );

    expect(screen.getByText("Picture-based embedded tracks do not count")).toBeInTheDocument();
  });
});

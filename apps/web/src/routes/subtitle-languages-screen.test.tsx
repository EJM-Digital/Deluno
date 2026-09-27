import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

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

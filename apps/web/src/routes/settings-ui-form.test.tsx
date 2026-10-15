import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * Settings > Interface: the colour-impaired preview, language, calendar, formats and default views, Save interface
 * settings with its footer states, and an open form following a value saved elsewhere.
 */

vi.setConfig({ testTimeout: 30_000 });
const setTheme = vi.hoisted(() => vi.fn());
vi.mock("next-themes", () => ({ useTheme: () => ({ setTheme }) }));
const setColorMode = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-color-mode", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/use-color-mode")>()), useColorMode: () => ({ colorMode: "standard", setColorMode }) }));
const replaceFromSettings = vi.hoisted(() => vi.fn());
vi.mock("../lib/display-preferences", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/display-preferences")>()),
  useDisplayPreferences: () => ({ preferences: { showRelativeDates: true }, replaceFromSettings })
}));
const mutate = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-api-mutation", () => ({ useApiMutation: () => ({ mutate, state: "idle", fieldErrors: {} }) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const liveRefresh = vi.hoisted(() => ({ spec: undefined as { events?: string[] } | undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: (spec: { events?: string[] }) => { liveRefresh.spec = spec; } }));

const loaderData = vi.hoisted(() => ({ settings: {} as Record<string, unknown> }));
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({ settings: loaderData.settings, libraries: [], qualityProfiles: [] })
}));

const { SettingsUiPage } = await import("./settings-ui-page");
const base = {
  ...emptyPlatformSettingsSnapshot, uiTheme: "system", uiColorMode: "standard", uiLanguage: "en-AU", calendarFirstDayOfWeek: "monday", calendarWeekHeaderFormat: "ddd d/M",
  runtimeFormat: "hm", timeFormat: "12h", shortDateFormat: "dmy", longDateFormat: "full", showRelativeDates: true, defaultMovieView: "grid", defaultShowView: "grid"
};
const renderPage = () => render(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
const choose = (group: string, label: string) => fireEvent.click(screen.getByRole("radiogroup", { name: group }).querySelector(`[role='radio'][aria-label='${label}'], [role='radio']`) as HTMLElement);
const pick = (name: string) => fireEvent.click(screen.getByRole("radio", { name }));

beforeEach(() => {
  cleanup();
  mutate.mockReset();
  setTheme.mockReset();
  setColorMode.mockReset();
  replaceFromSettings.mockReset();
  loaderData.settings = { ...base };
});

describe("colour-impaired mode", () => {
  it("previews the palette the moment it is clicked, before anything is saved", () => {
    renderPage();
    pick("Colour-impaired");
    expect(setColorMode).toHaveBeenCalledWith("impaired");
    expect(mutate).not.toHaveBeenCalled();
    expect(screen.getByText("Unsaved changes")).toBeInTheDocument();
  });

  it("is saved with Save interface settings and then restored from the saved value", async () => {
    mutate.mockResolvedValueOnce({ ...base, uiColorMode: "impaired" });
    renderPage();
    pick("Colour-impaired");
    fireEvent.click(screen.getByRole("button", { name: "Save interface settings" }));
    await waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(mutate.mock.calls[0][0]).toMatchObject({ uiColorMode: "impaired" });
    expect(setColorMode).toHaveBeenLastCalledWith("impaired");
  });
});

describe("language, calendar, formats and default views", () => {
  it("offers English (Australia) and English (United States)", () => {
    renderPage();
    expect(screen.getByRole("radio", { name: "English (Australia)" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "English (United States)" })).toBeInTheDocument();
  });

  it("shows the week header examples Tue 25/03, Tue 03/25 and Tue 25 Mar and the two first days of the week", () => {
    renderPage();
    for (const name of ["Tue 25/03", "Tue 03/25", "Tue 25 Mar", "Monday", "Sunday"]) {
      expect(screen.getByRole("radio", { name })).toBeInTheDocument();
    }
  });

  it("sends every field it shows in one PATCH when saved, with the choices made", async () => {
    mutate.mockResolvedValueOnce({ ...base, uiLanguage: "en-US", calendarFirstDayOfWeek: "sunday", defaultMovieView: "list", defaultShowView: "list" });
    renderPage();
    pick("English (United States)");
    pick("Sunday");
    pick("Tue 03/25");
    fireEvent.click(within_group("Default movies view", "List"));
    fireEvent.click(within_group("Default TV view", "List"));
    fireEvent.click(screen.getByRole("button", { name: "Save interface settings" }));
    await waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(mutate.mock.calls[0][0]).toMatchObject({
      uiTheme: "system", uiColorMode: "standard", uiLanguage: "en-US", calendarFirstDayOfWeek: "sunday", calendarWeekHeaderFormat: "ddd m/d",
      defaultMovieView: "list", defaultShowView: "list", showRelativeDates: true, showAbsoluteEpisodeNumbers: false
    });
    expect(Object.keys(mutate.mock.calls[0][0]).sort()).toEqual([
      "calendarFirstDayOfWeek", "calendarWeekHeaderFormat", "defaultMovieView", "defaultShowView", "longDateFormat", "runtimeFormat", "shortDateFormat",
      "showAbsoluteEpisodeNumbers", "showRelativeDates", "timeFormat", "timezone", "uiColorMode", "uiLanguage", "uiTheme"
    ]);
  });
});

describe("Save interface settings", () => {
  it("replaces the display preferences from the reply so lists and the calendar change at once, and says Saved just now", async () => {
    const reply = { ...base, uiLanguage: "en-US" };
    mutate.mockResolvedValueOnce(reply);
    renderPage();
    pick("English (United States)");
    fireEvent.click(screen.getByRole("button", { name: "Save interface settings" }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    expect(replaceFromSettings).toHaveBeenCalledWith(reply);
  });

  it("shows the error message in the footer when the save is refused", async () => {
    mutate.mockRejectedValueOnce(new Error("The language is not supported."));
    renderPage();
    pick("English (United States)");
    fireEvent.click(screen.getByRole("button", { name: "Save interface settings" }));
    expect(await screen.findByText("The language is not supported.")).toBeInTheDocument();
    expect(replaceFromSettings).not.toHaveBeenCalled();
  });
});

describe("staying current", () => {
  it("reads again when a setting changes elsewhere", () => {
    renderPage();
    expect(liveRefresh.spec?.events).toContain("SettingsChanged");
  });

  it("follows a value saved elsewhere into the open form while nothing here is edited, and the colour mode with it", () => {
    const view = renderPage();
    expect(screen.getByRole("radio", { name: "English (Australia)" })).toBeChecked();
    loaderData.settings = { ...base, uiLanguage: "en-US", uiColorMode: "impaired" };
    view.rerender(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
    expect(screen.getByRole("radio", { name: "English (United States)" })).toBeChecked();
    expect(setColorMode).toHaveBeenCalledWith("impaired");
  });
});

function within_group(group: string, label: string): HTMLElement {
  const container = screen.getByLabelText(group);
  return Array.from(container.querySelectorAll("[role='radio']")).find((node) => node.textContent?.trim() === label) as HTMLElement;
}
void choose;

import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * The saved theme used to go nowhere but the settings table: next-themes'
 * own localStorage was the only thing that actually drove light/dark/system,
 * so a theme chosen here disagreed with whatever was saved the moment the
 * page reloaded (#736). Saving now calls next-themes' setTheme too, the same
 * way colour mode already applies as you change it.
 */
const setTheme = vi.hoisted(() => vi.fn());
vi.mock("next-themes", () => ({ useTheme: () => ({ setTheme }) }));

const mutate = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-api-mutation", () => ({
  useApiMutation: () => ({ mutate, state: "idle", fieldErrors: {} })
}));

// Needs a data router (createBrowserRouter/useBlocker) this test does not set
// up; irrelevant to whether saving applies the theme.
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const settings = { ...emptyPlatformSettingsSnapshot, uiTheme: "system" };
let loaded: typeof settings = settings;
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({ settings: loaded, libraries: [], qualityProfiles: [] })
}));

const { SettingsUiPage } = await import("./settings-ui-page");

describe("interface settings apply the saved theme (#736)", () => {
  afterEach(() => {
    setTheme.mockReset();
    mutate.mockReset();
  });

  it("calls next-themes' setTheme with the chosen theme on save", async () => {
    mutate.mockResolvedValueOnce({ ...settings, uiTheme: "dark" });

    render(
      <MemoryRouter>
        <SettingsUiPage />
      </MemoryRouter>
    );

    fireEvent.change(screen.getByRole("combobox", { name: "Theme" }), { target: { value: "dark" } });
    fireEvent.click(screen.getByRole("button", { name: "Save interface settings" }));

    await vi.waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(setTheme).toHaveBeenCalledWith("dark");
  });
});

describe("absolute episode numbers for anime (#1024)", () => {
  afterEach(() => {
    mutate.mockReset();
  });

  it("is off until chosen, and saves the choice", async () => {
    mutate.mockResolvedValueOnce({ ...settings, showAbsoluteEpisodeNumbers: true });

    render(
      <MemoryRouter>
        <SettingsUiPage />
      </MemoryRouter>
    );

    const toggle = screen.getByRole("switch", { name: "Show absolute numbers for anime" });
    expect(toggle.getAttribute("aria-checked")).toBe("false");
    fireEvent.click(toggle);
    fireEvent.click(screen.getByRole("button", { name: "Save interface settings" }));

    await vi.waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(mutate.mock.calls[0]?.[0]).toMatchObject({ showAbsoluteEpisodeNumbers: true });
  });
});

describe("an edit made the moment a save finishes (#1503)", () => {
  afterEach(() => {
    mutate.mockReset();
  });

  it("is kept, instead of being put back to the old settings", async () => {
    mutate.mockResolvedValueOnce({ ...settings, uiTheme: "dark" });
    render(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
    const toggle = screen.getByRole("switch", { name: "Show absolute numbers for anime" });
    fireEvent.change(screen.getByRole("combobox", { name: "Theme" }), { target: { value: "dark" } });
    let edited = false;
    const observer = new MutationObserver(() => {
      if (edited || !screen.queryByText("Saved just now")) return;
      edited = true;
      fireEvent.click(toggle);
    });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    fireEvent.click(screen.getByRole("button", { name: "Save interface settings" }));
    await vi.waitFor(() => expect(edited).toBe(true));
    observer.disconnect();
    await vi.waitFor(() => expect(screen.getByText("Unsaved changes")).toBeVisible());
    expect(toggle.getAttribute("aria-checked")).toBe("true");
  });
});

describe("colour mode and failures on the Interface page", () => {
  afterEach(() => {
    mutate.mockReset();
    loaded = settings;
  });

  it("opens with the saved colour-impaired mode, and says Standard for a mode it does not know", () => {
    loaded = { ...settings, uiColorMode: "impaired" };
    const first = render(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
    expect(screen.getByRole("combobox", { name: "Colour-impaired mode" })).toHaveValue("impaired");
    first.unmount();
    loaded = { ...settings, uiColorMode: "sepia" };
    render(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
    expect(screen.getByRole("combobox", { name: "Colour-impaired mode" })).toHaveValue("standard");
  });

  it("follows a colour mode saved elsewhere while nothing here is edited, and falls back to Standard for an unknown one", () => {
    const view = render(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
    expect(screen.getByRole("combobox", { name: "Colour-impaired mode" })).toHaveValue("standard");
    loaded = { ...settings, uiColorMode: "impaired" };
    view.rerender(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
    expect(screen.getByRole("combobox", { name: "Colour-impaired mode" })).toHaveValue("impaired");
    loaded = { ...settings, uiColorMode: "sepia" };
    view.rerender(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
    expect(screen.getByRole("combobox", { name: "Colour-impaired mode" })).toHaveValue("standard");
  });

  it("uses plain words when the save fails with something that has no message", async () => {
    mutate.mockRejectedValueOnce("gone");
    render(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
    fireEvent.change(screen.getByRole("combobox", { name: "Theme" }), { target: { value: "dark" } });
    fireEvent.click(screen.getByRole("button", { name: "Save interface settings" }));
    expect(await screen.findByText("Could not save")).toBeInTheDocument();
  });

  it("sends one save when the form is submitted again while the first is still going", async () => {
    let finish: (value: unknown) => void = () => undefined;
    mutate.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    const view = render(<MemoryRouter><SettingsUiPage /></MemoryRouter>);
    fireEvent.change(screen.getByRole("combobox", { name: "Theme" }), { target: { value: "dark" } });
    const form = view.container.querySelector("form") as HTMLFormElement;
    fireEvent.submit(form);
    await screen.findByText("Saving…");
    fireEvent.submit(form);
    expect(mutate).toHaveBeenCalledTimes(1);
    finish({ ...settings, uiTheme: "dark" });
    await vi.waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
  });
});

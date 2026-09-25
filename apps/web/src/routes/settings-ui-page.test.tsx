import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * The saved theme used to go nowhere but the settings table: next-themes'
 * own localStorage was the only thing that actually drove light/dark/system,
 * so a theme chosen here disagreed with whatever was saved the moment the
 * page reloaded (#736). Saving now calls next-themes' setTheme too, the same
 * way density and colour mode already apply as you change them.
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
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({ settings, libraries: [], qualityProfiles: [] })
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

    fireEvent.click(screen.getByRole("radio", { name: "Dark" }));
    fireEvent.click(screen.getByRole("button", { name: "Save interface settings" }));

    await vi.waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(setTheme).toHaveBeenCalledWith("dark");
  });
});

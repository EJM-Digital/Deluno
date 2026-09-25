import { renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * The saved uiTheme used to have no effect at all: next-themes' own
 * localStorage was the only thing that ever set the applied theme, so it
 * never followed a saved choice to a different browser (#736).
 * DisplayPreferencesProvider already fetches settings once on app load for
 * date/time formatting; this proves that same load also calls next-themes'
 * setTheme with the saved value.
 */
const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("./api/client", () => ({ fetchJson }));

const setTheme = vi.hoisted(() => vi.fn());
vi.mock("next-themes", () => ({ useTheme: () => ({ setTheme }) }));

const { DisplayPreferencesProvider, useDisplayPreferences } = await import("./display-preferences");

function renderWithToken(token: string | null) {
  return renderHook(() => useDisplayPreferences(), {
    wrapper: ({ children }) => <DisplayPreferencesProvider token={token}>{children}</DisplayPreferencesProvider>
  });
}

describe("theme applies on app load from saved settings (#736)", () => {
  afterEach(() => {
    fetchJson.mockReset();
    setTheme.mockReset();
  });

  it("calls next-themes' setTheme with the saved theme once settings load", async () => {
    fetchJson.mockResolvedValueOnce({ uiTheme: "dark" });

    renderWithToken("a-token");

    await vi.waitFor(() => expect(setTheme).toHaveBeenCalledWith("dark"));
  });

  it("ignores a saved value next-themes was not configured to accept", async () => {
    fetchJson.mockResolvedValueOnce({ uiTheme: "not-a-real-theme" });

    renderWithToken("a-token");

    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(setTheme).not.toHaveBeenCalled();
  });

  it("does nothing before there is a token to fetch settings with", () => {
    renderWithToken(null);

    expect(fetchJson).not.toHaveBeenCalled();
    expect(setTheme).not.toHaveBeenCalled();
  });
});

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

// #1329: the compact date formats, and what the preferences provider does with /api/settings.

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("./api/client", () => ({ fetchJson }));
const setTheme = vi.hoisted(() => vi.fn());
vi.mock("next-themes", () => ({ useTheme: () => ({ setTheme }) }));

const { DEFAULT_DISPLAY_PREFERENCES, DisplayPreferencesProvider, formatMonth, formatRangeDate, formatWeekday, useDisplayPreferences } = await import("./display-preferences");

const day = new Date(2026, 2, 25, 13, 5); // a Wednesday, 25 March 2026, local time

describe("formatRangeDate, formatWeekday and formatMonth (#1329)", () => {
  it("formatRangeDate writes day then month, month then day, or the ISO day, as the short-date choice says", () => {
    expect(formatRangeDate(day, { ...DEFAULT_DISPLAY_PREFERENCES, shortDateFormat: "dmy" })).toMatch(/^25 Mar/);
    expect(formatRangeDate(day, { ...DEFAULT_DISPLAY_PREFERENCES, shortDateFormat: "mdy" })).toMatch(/^Mar 25/);
    expect(formatRangeDate(day, { ...DEFAULT_DISPLAY_PREFERENCES, shortDateFormat: "iso" })).toBe("2026-03-25");
    expect(formatRangeDate(new Date(2026, 0, 5), { ...DEFAULT_DISPLAY_PREFERENCES, shortDateFormat: "iso" })).toBe("2026-01-05");
  });

  it("formatRangeDate, formatWeekday and formatMonth show a dash for a value that is not a date", () => {
    expect(formatRangeDate(null)).toBe("—");
    expect(formatRangeDate(undefined)).toBe("—");
    expect(formatRangeDate("not a date")).toBe("—");
    expect(formatWeekday("not a date")).toBe("—");
    expect(formatMonth("not a date")).toBe("—");
  });

  it("formatWeekday names the day in full, and formatMonth the month in full with its year", () => {
    expect(formatWeekday(day)).toBe("Wednesday");
    expect(formatMonth(day)).toBe("March 2026");
  });

  it("formatWeekday and formatMonth follow the interface language", () => {
    const german = { ...DEFAULT_DISPLAY_PREFERENCES, uiLanguage: "de-DE" };
    expect(formatWeekday(day, german)).toBe("Mittwoch");
    expect(formatMonth(day, german)).toBe("März 2026");
  });
});

describe("the preferences provider reads /api/settings (#1329)", () => {
  afterEach(() => fetchJson.mockReset());

  const wrap = (token: string | null) => renderHook(() => useDisplayPreferences(), {
    wrapper: ({ children }) => <DisplayPreferencesProvider token={token}>{children}</DisplayPreferencesProvider>
  });

  it("reads the settings once after sign-in and carries the formatting choices and the certification country", async () => {
    fetchJson.mockResolvedValue({ shortDateFormat: "iso", timeFormat: "24", metadataCertificationCountry: " GB " });
    const { result, rerender } = wrap("a-token");

    await waitFor(() => expect(result.current.preferences.shortDateFormat).toBe("iso"));
    expect(result.current.preferences.timeFormat).toBe("24");
    expect(result.current.certificationCountry).toBe("GB");
    rerender();
    rerender();
    expect(fetchJson).toHaveBeenCalledTimes(1);
    expect(fetchJson).toHaveBeenCalledWith("/api/settings");
  });

  it("with no certification country saved it carries US", async () => {
    fetchJson.mockResolvedValue({ shortDateFormat: "mdy", metadataCertificationCountry: "   " });
    const { result } = wrap("a-token");

    await waitFor(() => expect(result.current.preferences.shortDateFormat).toBe("mdy"));
    expect(result.current.certificationCountry).toBe("US");
  });

  it("keeps the formatting defaults when the settings cannot be read", async () => {
    fetchJson.mockRejectedValue(new Error("offline"));
    const { result } = wrap("a-token");

    await waitFor(() => expect(fetchJson).toHaveBeenCalled());
    await Promise.resolve();
    expect(result.current.preferences).toEqual(DEFAULT_DISPLAY_PREFERENCES);
    expect(result.current.certificationCountry).toBe("US");
  });
});

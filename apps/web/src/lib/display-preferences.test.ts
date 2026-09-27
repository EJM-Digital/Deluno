import { describe, expect, it } from "vitest";
import {
  DEFAULT_DISPLAY_PREFERENCES,
  displayPreferencesFromSettings,
  formatAirDateTime,
  formatCalendarWeekHeader,
  formatDateTime,
  formatLongDate,
  formatRuntime,
  formatShortDate,
  formatTime,
  hasBroadcastTime
} from "./display-preferences";

const day = new Date(2026, 2, 25, 13, 5);
const sameDay = new Date(2026, 2, 25, 8);

describe("display preferences", () => {
  it("normalises stored values and keeps unknown values safe", () => {
    expect(
      displayPreferencesFromSettings({
        uiLanguage: " en-US ",
        calendarFirstDayOfWeek: "sunday",
        calendarWeekHeaderFormat: "ddd d mmm",
        runtimeFormat: "minutes",
        shortDateFormat: "iso",
        longDateFormat: "mdy",
        timeFormat: "24",
        showRelativeDates: false,
        timezone: " Australia/Sydney "
      })
    ).toEqual({
      uiLanguage: "en-US",
      calendarFirstDayOfWeek: "sunday",
      calendarWeekHeaderFormat: "ddd d mmm",
      runtimeFormat: "minutes",
      shortDateFormat: "iso",
      longDateFormat: "mdy",
      timeFormat: "24",
      showRelativeDates: false,
      timezone: "Australia/Sydney"
    });

    expect(displayPreferencesFromSettings({ runtimeFormat: "unexpected", timeFormat: "unexpected" })).toEqual(DEFAULT_DISPLAY_PREFERENCES);
  });

  it("uses relative labels only when the preference is enabled", () => {
    expect(formatShortDate(day, DEFAULT_DISPLAY_PREFERENCES, sameDay)).toBe("Today");
    expect(formatShortDate(day, { ...DEFAULT_DISPLAY_PREFERENCES, showRelativeDates: false }, sameDay)).toBe("25/03/2026");
    expect(formatShortDate(day, { ...DEFAULT_DISPLAY_PREFERENCES, shortDateFormat: "mdy", showRelativeDates: false }, sameDay)).toBe("03/25/2026");
    expect(formatShortDate(day, { ...DEFAULT_DISPLAY_PREFERENCES, shortDateFormat: "iso", showRelativeDates: false }, sameDay)).toBe("2026-03-25");
  });

  it("applies the shared long date, time and datetime formats", () => {
    const preferences = { ...DEFAULT_DISPLAY_PREFERENCES, showRelativeDates: false };
    expect(formatLongDate(day, preferences)).toContain("Wednesday");
    expect(formatLongDate(day, { ...preferences, longDateFormat: "mdy" })).toContain("March");
    expect(formatTime(day, { ...preferences, timeFormat: "24" })).toMatch(/13:05/);
    expect(formatDateTime(day, { ...preferences, shortDateFormat: "iso", timeFormat: "24" })).toBe("2026-03-25 · 13:05");
  });

  it("converts a stored broadcast time into the user's chosen timezone (#860)", () => {
    // 2026-03-25 05:00 UTC is still the evening before in US/Pacific (UTC-7
    // during US daylight saving) — the date itself shifts, not only the clock.
    const broadcastUtc = "2026-03-25T05:00:00Z";
    const preferences = { ...DEFAULT_DISPLAY_PREFERENCES, showRelativeDates: false, timeFormat: "24" as const };

    expect(formatTime(broadcastUtc, { ...preferences, timezone: "UTC" })).toBe("05:00");
    expect(formatTime(broadcastUtc, { ...preferences, timezone: "America/Los_Angeles" })).toBe("22:00");
    expect(formatShortDate(broadcastUtc, { ...preferences, timezone: "UTC" })).toBe("25/03/2026");
    expect(formatShortDate(broadcastUtc, { ...preferences, timezone: "America/Los_Angeles" })).toBe("24/03/2026");
  });

  it("falls back to the browser/OS timezone when none is chosen", () => {
    const broadcastUtc = "2026-03-25T09:30:00Z";
    const withZone = formatTime(broadcastUtc, { ...DEFAULT_DISPLAY_PREFERENCES, timeFormat: "24", timezone: "UTC" });
    const withoutZone = formatTime(broadcastUtc, { ...DEFAULT_DISPLAY_PREFERENCES, timeFormat: "24", timezone: "" });
    // The test runner's own zone is unknown, but it must not silently coerce to UTC.
    expect(withoutZone).toBe(new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", hour12: false }).format(new Date(broadcastUtc)));
    expect(typeof withZone).toBe("string");
  });

  it("only shows a time for an episode with a real broadcast time, not the midnight placeholder", () => {
    expect(hasBroadcastTime("2026-03-25T00:00:00Z")).toBe(false);
    expect(hasBroadcastTime("2026-03-25T20:00:00Z")).toBe(true);

    const preferences = { ...DEFAULT_DISPLAY_PREFERENCES, timezone: "UTC", timeFormat: "24" as const };
    expect(formatAirDateTime("2026-03-25T00:00:00Z", preferences)).toBe("25/03/2026");
    expect(formatAirDateTime("2026-03-25T20:00:00Z", preferences)).toBe("25/03/2026 · 20:00");
  });

  it("formats calendar headers and runtimes from the same vocabulary", () => {
    expect(formatCalendarWeekHeader(day, DEFAULT_DISPLAY_PREFERENCES)).toBe("Wed 25/03");
    expect(formatCalendarWeekHeader(day, { ...DEFAULT_DISPLAY_PREFERENCES, calendarWeekHeaderFormat: "ddd m/d" })).toBe("Wed 3/25");
    expect(formatCalendarWeekHeader(day, { ...DEFAULT_DISPLAY_PREFERENCES, calendarWeekHeaderFormat: "ddd d mmm" })).toBe("Wed 25 Mar");
    expect(formatRuntime(75, DEFAULT_DISPLAY_PREFERENCES)).toBe("1h 15m");
    expect(formatRuntime(75, { ...DEFAULT_DISPLAY_PREFERENCES, runtimeFormat: "minutes" })).toBe("75 minutes");
  });
});

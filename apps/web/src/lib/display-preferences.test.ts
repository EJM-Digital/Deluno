import { describe, expect, it } from "vitest";
import {
  DEFAULT_DISPLAY_PREFERENCES,
  displayPreferencesFromSettings,
  formatCalendarWeekHeader,
  formatDateTime,
  formatLongDate,
  formatRuntime,
  formatShortDate,
  formatTime
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
        showRelativeDates: false
      })
    ).toEqual({
      uiLanguage: "en-US",
      calendarFirstDayOfWeek: "sunday",
      calendarWeekHeaderFormat: "ddd d mmm",
      runtimeFormat: "minutes",
      shortDateFormat: "iso",
      longDateFormat: "mdy",
      timeFormat: "24",
      showRelativeDates: false
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

  it("formats calendar headers and runtimes from the same vocabulary", () => {
    expect(formatCalendarWeekHeader(day, DEFAULT_DISPLAY_PREFERENCES)).toBe("Wed 25/03");
    expect(formatCalendarWeekHeader(day, { ...DEFAULT_DISPLAY_PREFERENCES, calendarWeekHeaderFormat: "ddd m/d" })).toBe("Wed 3/25");
    expect(formatCalendarWeekHeader(day, { ...DEFAULT_DISPLAY_PREFERENCES, calendarWeekHeaderFormat: "ddd d mmm" })).toBe("Wed 25 Mar");
    expect(formatRuntime(75, DEFAULT_DISPLAY_PREFERENCES)).toBe("1h 15m");
    expect(formatRuntime(75, { ...DEFAULT_DISPLAY_PREFERENCES, runtimeFormat: "minutes" })).toBe("75 minutes");
  });
});

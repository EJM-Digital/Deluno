/**
 * User-facing date, time and runtime formatting.
 *
 * Components pass moments and minutes here rather than constructing their own
 * Intl formatters. That keeps the preference meaningful on every screen and
 * gives the settings page one small, testable vocabulary.
 */
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { fetchJson } from "./api/client";
import type { PlatformSettingsSnapshot } from "./api/types/settings";

export type CalendarFirstDay = "monday" | "sunday";
export type CalendarWeekHeaderFormat = "ddd d/M" | "ddd m/d" | "ddd d mmm";
export type RuntimeFormat = "hoursMinutes" | "minutes";
export type ShortDateFormat = "dmy" | "mdy" | "iso";
export type LongDateFormat = "full" | "mdy";
export type TimeFormat = "12" | "24";

export interface DisplayPreferences {
  uiLanguage: string;
  calendarFirstDayOfWeek: CalendarFirstDay;
  calendarWeekHeaderFormat: CalendarWeekHeaderFormat;
  runtimeFormat: RuntimeFormat;
  shortDateFormat: ShortDateFormat;
  longDateFormat: LongDateFormat;
  timeFormat: TimeFormat;
  showRelativeDates: boolean;
}

export const DEFAULT_DISPLAY_PREFERENCES: DisplayPreferences = {
  uiLanguage: "en-AU",
  calendarFirstDayOfWeek: "monday",
  calendarWeekHeaderFormat: "ddd d/M",
  runtimeFormat: "hoursMinutes",
  shortDateFormat: "dmy",
  longDateFormat: "full",
  timeFormat: "12",
  showRelativeDates: true
};

export function displayPreferencesFromSettings(settings: Partial<PlatformSettingsSnapshot> | null | undefined): DisplayPreferences {
  const weekHeader = settings?.calendarWeekHeaderFormat;
  const shortDate = settings?.shortDateFormat;
  const longDate = settings?.longDateFormat;
  return {
    uiLanguage: settings?.uiLanguage?.trim() || DEFAULT_DISPLAY_PREFERENCES.uiLanguage,
    calendarFirstDayOfWeek: settings?.calendarFirstDayOfWeek === "sunday" ? "sunday" : "monday",
    calendarWeekHeaderFormat: weekHeader === "ddd m/d" || weekHeader === "ddd d mmm" ? weekHeader : "ddd d/M",
    runtimeFormat: settings?.runtimeFormat === "minutes" ? "minutes" : "hoursMinutes",
    shortDateFormat: shortDate === "mdy" || shortDate === "iso" ? shortDate : "dmy",
    longDateFormat: longDate === "mdy" ? "mdy" : "full",
    timeFormat: settings?.timeFormat === "24" ? "24" : "12",
    showRelativeDates: settings?.showRelativeDates !== false
  };
}

function localeFor(preferences: DisplayPreferences) {
  return preferences.uiLanguage.trim() || "en-AU";
}

function parseDate(value: string | Date | null | undefined): Date | null {
  const date = value instanceof Date ? new Date(value) : value ? new Date(value) : null;
  return date && !Number.isNaN(date.getTime()) ? date : null;
}

function localDayNumber(date: Date) {
  return Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / 86_400_000;
}

function relativeDayLabel(date: Date, now: Date) {
  const difference = localDayNumber(date) - localDayNumber(now);
  if (difference === 0) return "Today";
  if (difference === -1) return "Yesterday";
  if (difference === 1) return "Tomorrow";
  return null;
}

export function formatShortDate(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES, now = new Date()) {
  const date = parseDate(value);
  if (!date) return "—";
  if (preferences.showRelativeDates) {
    const relative = relativeDayLabel(date, now);
    if (relative) return relative;
  }

  if (preferences.shortDateFormat === "iso") {
    return `${date.getFullYear().toString().padStart(4, "0")}-${(date.getMonth() + 1).toString().padStart(2, "0")}-${date.getDate().toString().padStart(2, "0")}`;
  }

  const year = date.getFullYear().toString().padStart(4, "0");
  const month = (date.getMonth() + 1).toString().padStart(2, "0");
  const day = date.getDate().toString().padStart(2, "0");
  return preferences.shortDateFormat === "mdy" ? `${month}/${day}/${year}` : `${day}/${month}/${year}`;
}

export function formatLongDate(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES, now = new Date()) {
  const date = parseDate(value);
  if (!date) return "—";
  if (preferences.showRelativeDates) {
    const relative = relativeDayLabel(date, now);
    if (relative) return relative;
  }

  return new Intl.DateTimeFormat(
    preferences.longDateFormat === "mdy" ? "en-US" : localeFor(preferences),
    { weekday: "long", month: "long", day: "numeric", year: "numeric" }
  ).format(date);
}

export function formatTime(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES) {
  const date = parseDate(value);
  if (!date) return "—";
  return new Intl.DateTimeFormat(
    localeFor(preferences),
    preferences.timeFormat === "24"
      ? { hour: "2-digit", minute: "2-digit", hour12: false }
      : { hour: "numeric", minute: "2-digit", hour12: true }
  ).format(date);
}

export function formatDateTime(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES, now = new Date()) {
  const date = parseDate(value);
  if (!date) return "—";
  const relative = preferences.showRelativeDates ? relativeDayLabel(date, now) : null;
  return `${relative ?? formatShortDate(date, { ...preferences, showRelativeDates: false }, now)} · ${formatTime(date, preferences)}`;
}

export function formatCalendarWeekHeader(value: string | Date, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES) {
  const date = parseDate(value);
  if (!date) return "—";
  const weekday = new Intl.DateTimeFormat(localeFor(preferences), { weekday: "short" }).format(date);
  const day = date.getDate().toString().padStart(2, "0");
  const month = (date.getMonth() + 1).toString().padStart(2, "0");
  if (preferences.calendarWeekHeaderFormat === "ddd m/d") return `${weekday} ${date.getMonth() + 1}/${date.getDate()}`;
  if (preferences.calendarWeekHeaderFormat === "ddd d mmm") {
    return `${weekday} ${date.getDate()} ${new Intl.DateTimeFormat(localeFor(preferences), { month: "short" }).format(date)}`;
  }
  return `${weekday} ${day}/${month}`;
}

export function formatWeekday(value: string | Date, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES) {
  const date = parseDate(value);
  return date ? new Intl.DateTimeFormat(localeFor(preferences), { weekday: "long" }).format(date) : "—";
}

export function formatMonth(value: string | Date, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES) {
  const date = parseDate(value);
  return date ? new Intl.DateTimeFormat(localeFor(preferences), { month: "long", year: "numeric" }).format(date) : "—";
}

/** A compact date used for chart axes and calendar range edges. */
export function formatRangeDate(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES) {
  const date = parseDate(value);
  if (!date) return "—";
  if (preferences.shortDateFormat === "iso") {
    return `${date.getFullYear().toString().padStart(4, "0")}-${(date.getMonth() + 1).toString().padStart(2, "0")}-${date.getDate().toString().padStart(2, "0")}`;
  }

  const month = new Intl.DateTimeFormat(localeFor(preferences), { month: "short" }).format(date);
  return preferences.shortDateFormat === "mdy"
    ? `${month} ${date.getDate()}`
    : `${date.getDate()} ${month}`;
}

export function formatRuntime(minutes: number | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES) {
  if (minutes === null || minutes === undefined || !Number.isFinite(minutes)) return "—";
  const rounded = Math.max(0, Math.round(minutes));
  if (preferences.runtimeFormat === "minutes") return `${rounded} minutes`;
  const hours = Math.floor(rounded / 60);
  const remainder = rounded % 60;
  return hours > 0 ? `${hours}h ${remainder.toString().padStart(2, "0")}m` : `${remainder}m`;
}

interface DisplayPreferencesContextValue {
  preferences: DisplayPreferences;
  replaceFromSettings: (settings: Partial<PlatformSettingsSnapshot>) => void;
}

const DisplayPreferencesContext = createContext<DisplayPreferencesContextValue | null>(null);

export function DisplayPreferencesProvider({ token, children }: { token: string | null; children: ReactNode }) {
  const [preferences, setPreferences] = useState<DisplayPreferences>(DEFAULT_DISPLAY_PREFERENCES);

  const replaceFromSettings = useCallback((settings: Partial<PlatformSettingsSnapshot>) => {
    setPreferences(displayPreferencesFromSettings(settings));
  }, []);

  useEffect(() => {
    if (!token) return;
    let cancelled = false;
    fetchJson<PlatformSettingsSnapshot>("/api/settings")
      .then((settings) => {
        if (!cancelled) replaceFromSettings(settings);
      })
      .catch(() => {
        /* Formatting defaults are safe when settings are temporarily unavailable. */
      });
    return () => {
      cancelled = true;
    };
  }, [replaceFromSettings, token]);

  const value = useMemo(() => ({ preferences, replaceFromSettings }), [preferences, replaceFromSettings]);
  return <DisplayPreferencesContext.Provider value={value}>{children}</DisplayPreferencesContext.Provider>;
}

export function useDisplayPreferences() {
  const context = useContext(DisplayPreferencesContext);
  return context ?? { preferences: DEFAULT_DISPLAY_PREFERENCES, replaceFromSettings: () => undefined };
}

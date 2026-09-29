/**
 * User-facing date, time and runtime formatting.
 *
 * Components pass moments and minutes here rather than constructing their own
 * Intl formatters. That keeps the preference meaningful on every screen and
 * gives the settings page one small, testable vocabulary.
 */
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useTheme } from "next-themes";
import { fetchJson } from "./api/client";
import type { PlatformSettingsSnapshot } from "./api/types/settings";

/** The only values next-themes' ThemeProvider (see main.tsx) is configured to accept. */
function isAppliedTheme(value: unknown): value is "system" | "dark" | "light" {
  return value === "system" || value === "dark" || value === "light";
}

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
  /** #1024: write an anime episode as S01E05 (105), with its absolute number. Off until chosen. */
  showAbsoluteEpisodeNumbers: boolean;
  /**
   * The IANA zone (e.g. "Australia/Sydney") an air date/time is converted
   * into before it is shown (#860). Empty means none chosen — every
   * formatter below then omits `timeZone` from its `Intl.DateTimeFormat`
   * options entirely, which is what makes `Intl` fall back to the
   * browser/OS zone rather than UTC.
   */
  timezone: string;
}

export const DEFAULT_DISPLAY_PREFERENCES: DisplayPreferences = {
  uiLanguage: "en-AU",
  calendarFirstDayOfWeek: "monday",
  calendarWeekHeaderFormat: "ddd d/M",
  runtimeFormat: "hoursMinutes",
  shortDateFormat: "dmy",
  longDateFormat: "full",
  timeFormat: "12",
  showRelativeDates: true,
  showAbsoluteEpisodeNumbers: false,
  timezone: ""
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
    showRelativeDates: settings?.showRelativeDates !== false,
    showAbsoluteEpisodeNumbers: settings?.showAbsoluteEpisodeNumbers === true,
    timezone: settings?.timezone?.trim() || ""
  };
}

function localeFor(preferences: DisplayPreferences) {
  return preferences.uiLanguage.trim() || "en-AU";
}

function parseDate(value: string | Date | null | undefined): Date | null {
  const date = value instanceof Date ? new Date(value) : value ? new Date(value) : null;
  return date && !Number.isNaN(date.getTime()) ? date : null;
}

/**
 * The calendar date/time parts `date` falls on in `timeZone` (empty = the
 * browser/OS zone). A date's year/month/day/hour/minute all shift with the
 * zone, which is why "Today" and the day component below cannot simply read
 * `date.getFullYear()` etc — those are always in the browser's own zone,
 * making the local-time short-date reasoning silently wrong whenever a
 * timezone preference is set (#860).
 */
function zonedParts(date: Date, timeZone: string) {
  const formatter = new Intl.DateTimeFormat("en-US", {
    ...(timeZone ? { timeZone } : {}),
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false
  });
  const parts = Object.fromEntries(formatter.formatToParts(date).map((part) => [part.type, part.value]));
  // Midnight in `en-US`'s 24-hour output can read "24"; JS wants "0".
  const hour = Number(parts.hour) % 24;
  return {
    year: Number(parts.year),
    month: Number(parts.month),
    day: Number(parts.day),
    hour,
    minute: Number(parts.minute)
  };
}

function localDayNumber(date: Date, timeZone = "") {
  const parts = zonedParts(date, timeZone);
  return Date.UTC(parts.year, parts.month - 1, parts.day) / 86_400_000;
}

function relativeDayLabel(date: Date, now: Date, timeZone = "") {
  const difference = localDayNumber(date, timeZone) - localDayNumber(now, timeZone);
  if (difference === 0) return "Today";
  if (difference === -1) return "Yesterday";
  if (difference === 1) return "Tomorrow";
  return null;
}

export function formatShortDate(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES, now = new Date()) {
  const date = parseDate(value);
  if (!date) return "—";
  if (preferences.showRelativeDates) {
    const relative = relativeDayLabel(date, now, preferences.timezone);
    if (relative) return relative;
  }

  const { year, month, day } = zonedParts(date, preferences.timezone);
  if (preferences.shortDateFormat === "iso") {
    return `${year.toString().padStart(4, "0")}-${month.toString().padStart(2, "0")}-${day.toString().padStart(2, "0")}`;
  }

  const yearText = year.toString().padStart(4, "0");
  const monthText = month.toString().padStart(2, "0");
  const dayText = day.toString().padStart(2, "0");
  return preferences.shortDateFormat === "mdy" ? `${monthText}/${dayText}/${yearText}` : `${dayText}/${monthText}/${yearText}`;
}

export function formatLongDate(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES, now = new Date()) {
  const date = parseDate(value);
  if (!date) return "—";
  if (preferences.showRelativeDates) {
    const relative = relativeDayLabel(date, now, preferences.timezone);
    if (relative) return relative;
  }

  return new Intl.DateTimeFormat(
    preferences.longDateFormat === "mdy" ? "en-US" : localeFor(preferences),
    {
      ...(preferences.timezone ? { timeZone: preferences.timezone } : {}),
      weekday: "long",
      month: "long",
      day: "numeric",
      year: "numeric"
    }
  ).format(date);
}

/** The clock time `value` falls on, converted into the user's chosen timezone (browser/OS zone when none is set) (#860). */
export function formatTime(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES) {
  const date = parseDate(value);
  if (!date) return "—";
  return new Intl.DateTimeFormat(
    localeFor(preferences),
    {
      ...(preferences.timezone ? { timeZone: preferences.timezone } : {}),
      ...(preferences.timeFormat === "24"
        ? { hour: "2-digit", minute: "2-digit", hour12: false }
        : { hour: "numeric", minute: "2-digit", hour12: true })
    }
  ).format(date);
}

export function formatDateTime(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES, now = new Date()) {
  const date = parseDate(value);
  if (!date) return "—";
  const relative = preferences.showRelativeDates ? relativeDayLabel(date, now, preferences.timezone) : null;
  return `${relative ?? formatShortDate(date, { ...preferences, showRelativeDates: false }, now)} · ${formatTime(date, preferences)}`;
}

/**
 * Whether `airDateUtc` carries a real broadcast time (#860) rather than the
 * midnight-UTC placeholder Deluno stores when no source had one. Read once
 * here rather than at every call site, the same way `ReadAlternateTitles` is
 * the one place that reads `AlternateTitles` back out of stored metadata.
 */
export function hasBroadcastTime(airDateUtc: string | Date | null | undefined) {
  const date = parseDate(airDateUtc);
  return date !== null && (date.getUTCHours() !== 0 || date.getUTCMinutes() !== 0);
}

/** An episode's air date, with its real broadcast time appended when one is stored, in the user's chosen timezone (#860). */
export function formatAirDateTime(value: string | Date | null | undefined, preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES, now = new Date()) {
  if (!hasBroadcastTime(value)) {
    return formatShortDate(value, { ...preferences, showRelativeDates: false }, now);
  }

  return formatDateTime(value, { ...preferences, showRelativeDates: false }, now);
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
  /**
   * `metadata.certificationCountry` (#862) — carried here rather than
   * fetched again on the detail pages that need it, because this provider
   * already fetches `/api/settings` once on app load for the rest of
   * `DisplayPreferences`. Defaults to "US", the same fallback
   * `ResolveCertification` uses server-side when nothing else applies.
   */
  certificationCountry: string;
}

const DisplayPreferencesContext = createContext<DisplayPreferencesContextValue | null>(null);

export function DisplayPreferencesProvider({ token, children }: { token: string | null; children: ReactNode }) {
  const [preferences, setPreferences] = useState<DisplayPreferences>(DEFAULT_DISPLAY_PREFERENCES);
  const [certificationCountry, setCertificationCountry] = useState("US");
  const { setTheme } = useTheme();

  const replaceFromSettings = useCallback((settings: Partial<PlatformSettingsSnapshot>) => {
    setPreferences(displayPreferencesFromSettings(settings));
    setCertificationCountry(settings.metadataCertificationCountry?.trim() || "US");
  }, []);

  // The saved theme used to sit in Settings -> Interface and nowhere else:
  // next-themes' own localStorage was the only thing that ever set it, so a
  // theme chosen on one browser never followed to another (#736). This is the
  // one place that already fetches settings once on app load, so apply the
  // saved theme here too, right after that load — the same load this
  // provider exists for.
  useEffect(() => {
    if (!token) return;
    let cancelled = false;
    fetchJson<PlatformSettingsSnapshot>("/api/settings")
      .then((settings) => {
        if (cancelled) return;
        replaceFromSettings(settings);
        if (isAppliedTheme(settings.uiTheme)) setTheme(settings.uiTheme);
      })
      .catch(() => {
        /* Formatting defaults are safe when settings are temporarily unavailable. */
      });
    return () => {
      cancelled = true;
    };
  }, [replaceFromSettings, setTheme, token]);

  const value = useMemo(
    () => ({ preferences, replaceFromSettings, certificationCountry }),
    [preferences, replaceFromSettings, certificationCountry]
  );
  return <DisplayPreferencesContext.Provider value={value}>{children}</DisplayPreferencesContext.Provider>;
}

export function useDisplayPreferences() {
  const context = useContext(DisplayPreferencesContext);
  return context ?? { preferences: DEFAULT_DISPLAY_PREFERENCES, replaceFromSettings: () => undefined, certificationCountry: "US" };
}

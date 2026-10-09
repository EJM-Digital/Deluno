/**
 * GitHub's hourly limit tells when it lifts. The server writes that as a UTC stamp ("2026-10-09 00:35 UTC"),
 * because its own clock zone means nothing to whoever is looking from elsewhere (#1181). This shows it the way the
 * viewer's own clock reads it ("11:35 am", with the day when that is not today).
 */
const STAMP = /\b(\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2}) UTC\b/g;

/** One instant as the viewer's local time of day, and the weekday when it is not today. */
export function formatResetTime(instant: Date, now: Date = new Date()): string {
  const time = instant.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  return instant.toDateString() === now.toDateString()
    ? time
    : `${instant.toLocaleDateString([], { weekday: "short" })} ${time}`;
}

/** Replaces every UTC stamp in a server sentence with the viewer's local time. Text without a stamp is unchanged. */
export function localizeResetTimes(text: string, now: Date = new Date()): string {
  return text.replace(STAMP, (_match, year, month, day, hour, minute) =>
    formatResetTime(new Date(Date.UTC(Number(year), Number(month) - 1, Number(day), Number(hour), Number(minute))), now));
}

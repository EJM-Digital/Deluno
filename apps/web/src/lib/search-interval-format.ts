/**
 * Plain words for a library search interval (#984). The value on the wire is
 * always minutes — this only decides how to say it: minutes below an hour,
 * "Every N hours" (or "Every hour") once it divides evenly into hours, and
 * "Daily" at exactly a day. Anything else (an odd number that crosses an
 * hour boundary, e.g. 90) still reads in minutes rather than showing a
 * fraction nobody asked for.
 */
export function formatSearchInterval(minutes: number): string {
  if (!Number.isFinite(minutes) || minutes <= 0) {
    return "Paused";
  }
  if (minutes === 1440) {
    return "Daily";
  }
  if (minutes < 60 || minutes % 60 !== 0) {
    return `Every ${minutes} minute${minutes === 1 ? "" : "s"}`;
  }
  const hours = minutes / 60;
  return hours === 1 ? "Every hour" : `Every ${hours} hours`;
}

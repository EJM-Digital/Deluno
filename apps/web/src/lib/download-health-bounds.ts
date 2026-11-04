/**
 * #1475: the one place the download health bounds live on the web side. Every time a person sets (strike spacing,
 * no-progress, too-slow, low-peers) takes 1 to 999 minutes and every strike count takes 1 to 999. The server holds the
 * same numbers in Deluno.Contracts DownloadHealthBounds, which refuses a value outside them.
 */
export const DOWNLOAD_HEALTH_MIN_MINUTES = 1;
export const DOWNLOAD_HEALTH_MAX_MINUTES = 999;
export const DOWNLOAD_HEALTH_MIN_STRIKES = 1;
export const DOWNLOAD_HEALTH_MAX_STRIKES = 999;

/** The plain hint under a minutes field. */
export const DOWNLOAD_HEALTH_MINUTES_RANGE = `${DOWNLOAD_HEALTH_MIN_MINUTES} to ${DOWNLOAD_HEALTH_MAX_MINUTES} minutes`;
/** The plain hint under a strikes field. */
export const DOWNLOAD_HEALTH_STRIKES_RANGE = `${DOWNLOAD_HEALTH_MIN_STRIKES} to ${DOWNLOAD_HEALTH_MAX_STRIKES}`;

export function clampDownloadHealthMinutes(value: number): number {
  return Math.max(DOWNLOAD_HEALTH_MIN_MINUTES, Math.min(DOWNLOAD_HEALTH_MAX_MINUTES, value));
}

export function clampDownloadHealthStrikes(value: number): number {
  return Math.max(DOWNLOAD_HEALTH_MIN_STRIKES, Math.min(DOWNLOAD_HEALTH_MAX_STRIKES, value));
}

export function downloadHealthMinutesProblem(value: number, what: string): string | undefined {
  return Number.isInteger(value) && value >= DOWNLOAD_HEALTH_MIN_MINUTES && value <= DOWNLOAD_HEALTH_MAX_MINUTES
    ? undefined
    : `${what} must be between ${DOWNLOAD_HEALTH_MIN_MINUTES} and ${DOWNLOAD_HEALTH_MAX_MINUTES} minutes.`;
}

export function downloadHealthStrikesProblem(value: number, what: string): string | undefined {
  return Number.isInteger(value) && value >= DOWNLOAD_HEALTH_MIN_STRIKES && value <= DOWNLOAD_HEALTH_MAX_STRIKES
    ? undefined
    : `${what} must be between ${DOWNLOAD_HEALTH_MIN_STRIKES} and ${DOWNLOAD_HEALTH_MAX_STRIKES}.`;
}

/**
 * A tab running an older build of Deluno after an update (#622).
 *
 * Every screen is its own file named after its content, and an update replaces
 * those files. A tab opened before the update still asks for the old names,
 * gets a 404, and the screen failed with the browser's raw "Failed to fetch
 * dynamically imported module". A reload fetches the new build, because the
 * service worker takes pages from the network first, so a tab in that state
 * reloads itself, once.
 */

const RELOADED_AT_KEY = "deluno-stale-build-reloaded-at";

/**
 * How long one reload counts for. A second failure inside this window means the
 * reload did not help, so the page says so rather than reloading for ever.
 */
export const STALE_BUILD_RELOAD_WINDOW_MS = 60_000;

// Chrome and Edge, Firefox, and Safari word the same failure differently.
const STALE_BUILD_MESSAGE =
  /Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed|Unable to preload CSS/i;

export function isStaleBuildError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : typeof error === "string" ? error : "";
  return STALE_BUILD_MESSAGE.test(message);
}

type Reloadable = {
  sessionStorage: Pick<Storage, "getItem" | "setItem">;
  location: Pick<Location, "reload" | "assign">;
};

/**
 * Reloads onto the new build unless this tab already did so in the last
 * minute. Returns whether it reloaded. With a destination, the page that was
 * being opened, it loads that instead of the page the click came from.
 */
export function reloadOntoNewBuild(target: Reloadable = window, now: number = Date.now(), destination?: string | null): boolean {
  try {
    const last = Number(target.sessionStorage.getItem(RELOADED_AT_KEY));
    if (Number.isFinite(last) && last > 0 && now - last < STALE_BUILD_RELOAD_WINDOW_MS) {
      return false;
    }
    target.sessionStorage.setItem(RELOADED_AT_KEY, String(now));
  } catch {
    // No session storage means no loop guard, so do not reload unguarded.
    return false;
  }

  if (destination) {
    target.location.assign(destination);
  } else {
    target.location.reload();
  }
  return true;
}

/**
 * Vite reports a screen's file that failed to load as `vite:preloadError`
 * before the import rejects. Reloading there means the error screen is never
 * seen.
 */
export function installStaleBuildRecovery(
  target: EventTarget & Reloadable = window,
  pendingDestination: () => string | null = () => null
): void {
  target.addEventListener("vite:preloadError", (event) => {
    // Seen on the rig: a click on Media Management reloaded onto the page the
    // click came from, because the screen being opened was the one that failed.
    if (reloadOntoNewBuild(target, Date.now(), pendingDestination())) {
      event.preventDefault();
    }
  });
}

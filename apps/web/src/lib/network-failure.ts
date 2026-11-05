/**
 * What a handler says when a request failed to get an answer at all, in plain words (#1474).
 *
 * The browser rejects a fetch that nothing answered (the browser is offline, or Deluno is not running) with a `TypeError`
 * whose message is its own "Failed to fetch" (Chrome), "Load failed" (Safari) or "NetworkError when attempting to fetch
 * resource." (Firefox). That is not a sentence a person can act on. Every handler that shows a caught error's message goes
 * through here, so a lost connection reads the same on every screen.
 */
export const COULD_NOT_REACH_DELUNO = "Deluno could not be reached. Check that it is running, then try again.";

/** True for the rejection the browser gives a request that nothing answered. */
export function isNetworkFailure(error: unknown): boolean {
  return error instanceof TypeError;
}

/**
 * The sentence for a caught error: a lost connection in plain words, any other error's own message, and the handler's own
 * sentence when the error carries none (an empty message, or something that is not an Error).
 */
export function plainFailureMessage(error: unknown, fallback: string): string {
  if (isNetworkFailure(error)) return COULD_NOT_REACH_DELUNO;
  return error instanceof Error && error.message ? error.message : fallback;
}

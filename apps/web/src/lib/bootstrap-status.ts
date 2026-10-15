/** The first-run check could not be answered, so Deluno cannot say whether sign-in or setup comes first. */
export class BootstrapStatusError extends Error {
  constructor() {
    super("Deluno could not check whether it has been set up yet. Make sure Deluno is running, then try again.");
    this.name = "BootstrapStatusError";
  }
}

const RETRY_DELAYS_MS = [250, 600, 1200];

/**
 * Whether this install still needs its first account.
 *
 * A read that fails, is refused (the host answers 503 while it finishes starting) or is cut off by the router's own
 * next navigation is not an answer, so it is tried again and, when it still fails, reported. It is never taken as
 * "already set up": that sent a fresh install to the sign-in page and lost where the person was going (#1339).
 */
export async function readRequiresSetup(
  fetcher: typeof fetch = (...args) => fetch(...args),
  wait: (ms: number) => Promise<void> = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
): Promise<boolean> {
  for (let attempt = 0; ; attempt++) {
    try {
      const response = await fetcher("/api/auth/bootstrap-status");
      if (response.ok) {
        const payload = (await response.json()) as { requiresSetup?: unknown };
        if (typeof payload.requiresSetup === "boolean") return payload.requiresSetup;
      }
    } catch {
      // Aborted, offline or not JSON: the same as no answer.
    }

    if (attempt >= RETRY_DELAYS_MS.length) throw new BootstrapStatusError();
    await wait(RETRY_DELAYS_MS[attempt]);
  }
}

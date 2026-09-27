import { expect, type Page, type Request } from "@playwright/test";

/**
 * Playwright's "networkidle" (nothing in flight for 500 ms), minus the live
 * hub.
 *
 * The hub keeps one request open for as long as the page is: SignalR's SSE
 * stream, when WebSockets are not available, as behind the smoke preview. So
 * a page that is connected never goes network-idle, however finished its data
 * is. This waits for what "networkidle" was standing in for: every request for
 * the page's data has come back.
 *
 * Call it before the navigation, so it sees the requests that navigation
 * starts, and wait on it after.
 */
export function trackDataRequests(page: Page) {
  const inFlight = new Map<Request, number>();
  const settle = (request: Request) => inFlight.delete(request);
  page.on("request", (request) => {
    if (!new URL(request.url()).pathname.startsWith("/hubs/")) {
      inFlight.set(request, Date.now());
    }
  });
  page.on("requestfinished", settle);
  page.on("requestfailed", settle);

  return {
    async settled(quietMs = 500) {
      let quietSince = Date.now();
      try {
        // Like waitForLoadState, bounded by the test's own timeout, not its own.
        await expect.poll(() => {
          if (inFlight.size > 0) {
            quietSince = Date.now();
          }
          return Date.now() - quietSince >= quietMs;
        }, { intervals: [100], timeout: 0 }).toBe(true);
      } catch (error) {
        // A bare "timed out waiting for expect.poll" says nothing about why.
        // Name what was still open and for how long, so a CI-only hang shows
        // its cause in the failure message instead of just a stuck timer.
        if (inFlight.size > 0) {
          const now = Date.now();
          const stuck = Array.from(inFlight.entries())
            .map(([request, startedAt]) => `${request.method()} ${request.url()} (in flight ${now - startedAt}ms)`)
            .join("\n  ");
          throw new Error(`settled() timed out with ${inFlight.size} request(s) still in flight:\n  ${stuck}`, { cause: error });
        }
        throw error;
      }
    }
  };
}

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
  const inFlight = new Set<Request>();
  const settle = (request: Request) => inFlight.delete(request);
  page.on("request", (request) => {
    if (!new URL(request.url()).pathname.startsWith("/hubs/")) {
      inFlight.add(request);
    }
  });
  page.on("requestfinished", settle);
  page.on("requestfailed", settle);

  return {
    async settled(quietMs = 500) {
      let quietSince = Date.now();
      // Like waitForLoadState, bounded by the test's own timeout, not its own.
      await expect.poll(() => {
        if (inFlight.size > 0) {
          quietSince = Date.now();
        }
        return Date.now() - quietSince >= quietMs;
      }, { intervals: [100], timeout: 0 }).toBe(true);
    }
  };
}

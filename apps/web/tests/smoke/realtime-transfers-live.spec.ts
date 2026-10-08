import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { serveLiveBusy } from "../helpers/live-busy";

// #1179: Transfers changes by itself. The page joins the hub, and a download's
// progress and a sent download's stage reach the screen with no reload and no
// Refresh button. Only the REST lists are stubbed (serveLiveBusy); the pushes
// are raised by the server through the Testing-only endpoints
// (Testing:EnableRealtimePushEndpoint).

/** Calls a Testing-only endpoint as the signed-in user. */
async function server(page: Page, path: string, body?: unknown) {
  return page.evaluate(async ({ path, body }) => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch(path, {
      method: body === undefined ? "GET" : "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    return { status: response.status, json: response.status === 200 ? await response.json() : null };
  }, { path, body });
}

test.describe("Transfers is live (#1179)", () => {
  test("a download's progress and a sent download's stage change with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    await serveLiveBusy(page);
    let reloads = 0;
    page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) reloads += 1; });
    await page.goto("/queue");
    await expect(page.getByText(/Dune/).first()).toBeVisible();
    const navigationsAfterLoad = reloads;

    // There is nothing to press: the page keeps itself up to date.
    await expect(page.getByRole("button", { name: /^Refresh$/ })).toHaveCount(0);

    // The page joined the queue and live subjects.
    await expect.poll(async () => ((await server(page, "/api/testing/realtime/watchers")).json as { live: number; queue: number }).live, { timeout: 15_000 }).toBeGreaterThan(0);

    // Progress: Dune Part Two was at 61%.
    await expect(page.getByText("61%").first()).toBeVisible();
    expect((await server(page, "/api/testing/realtime/download-progress", { id: "q1", progress: 77, speedMbps: 9.5, status: "downloading" })).status).toBe(204);
    await expect(page.getByText("77%").first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("9.5 MB/s").first()).toBeVisible();

    // Stage: Oppenheimer's sent download is detected by the client.
    await expect(page.getByText("Detected by client")).toHaveCount(0);
    expect((await server(page, "/api/testing/realtime/live-event", {
      eventName: "JourneyStageChanged",
      payload: { dispatchId: "d2", releaseName: "Oppenheimer.2023.1080p.WEB-DL", mediaType: "movies", entityType: "movie", entityId: "m2", libraryId: "l1", stage: "detection_succeeded", detail: null, at: new Date().toISOString() }
    })).status).toBe(204);
    await expect(page.getByText("Detected by client").first()).toBeVisible({ timeout: 10_000 });

    expect(reloads, "the page reloaded or navigated").toBe(navigationsAfterLoad);
  });
});

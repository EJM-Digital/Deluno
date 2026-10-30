import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { serveLiveBusy } from "../helpers/live-busy";

// #1442: the Dashboard's first lane is called Searching and shows every title Deluno is searching for right now.
// The server raises SearchesInFlightChanged through the Testing-only endpoint (the same event the search handler
// pushes when a search begins and ends); the card appears and goes by itself, with no reload and no timer.

async function push(page: Page, payload: unknown) {
  return page.evaluate(async (body) => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch("/api/testing/realtime/live-event", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: JSON.stringify({ eventName: "SearchesInFlightChanged", payload: body })
    });
    return response.status;
  }, payload);
}

test.describe("the Searching lane is live (#1442)", () => {
  test("is named Searching, shows a title while its search runs and takes it off when the search ends, with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    await serveLiveBusy(page);
    let reloads = 0;
    page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) reloads += 1; });
    await page.goto("/");
    const board = page.getByRole("region", { name: /^pipeline/i });
    await expect(board).toBeVisible();
    // The phone layout draws the lanes as a list of cards, not as the ribbon of stations.
    if (test.info().project.name !== "mobile") {
      await expect(board.getByRole("group", { name: "Searching" }).first()).toBeAttached();
    }
    await expect(board.getByRole("group", { name: "Requested" })).toHaveCount(0);
    const navigationsAfterLoad = reloads;

    // The page has really joined the live subject before the server pushes.
    await expect.poll(async () => page.evaluate(async () => {
      const token = localStorage.getItem("deluno-auth-token");
      const response = await fetch("/api/testing/realtime/watchers", { headers: { Authorization: `Bearer ${token}` } });
      return response.status === 200 ? (await response.json()).live : 0;
    }), { timeout: 15_000 }).toBeGreaterThan(0);

    const now = new Date().toISOString();
    const search = { entityType: "movie", entityId: "m-probe", title: "Searching Probe", startedBy: "manual", label: "Checking 3 indexers", startedUtc: now };
    expect(await push(page, { searches: [search], at: now })).toBe(204);
    await expect(board.getByText("Searching Probe")).toBeVisible({ timeout: 10_000 });

    expect(await push(page, { searches: [], at: new Date().toISOString() })).toBe(204);
    await expect(board.getByText("Searching Probe")).toHaveCount(0, { timeout: 10_000 });

    expect(reloads, "the page reloaded or navigated").toBe(navigationsAfterLoad);
  });
});

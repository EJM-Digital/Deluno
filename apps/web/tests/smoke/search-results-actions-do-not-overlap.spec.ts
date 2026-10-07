import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1172: a result row's status, Block and Grab were drawn on top of each other,
// so a click on "Grab" could land on Block. A free search now has one row
// button, Grab, and Block lives in the result's details.
test("clicking Grab on a free-search result opens the Grab panel, not Block", async ({ page }) => {
  await page.route("**/api/search", async (route) => {
    if (route.request().method() !== "POST") { await route.continue(); return; }
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        indexersQueried: 1,
        candidates: [{
          releaseName: "The.Simpsons.S38E01.1080p.WEB-DL",
          indexerId: "idx-1",
          indexerName: "Fixture",
          quality: "WEB 1080p",
          meetsCutoff: true,
          decisionStatus: "rejected",
          sizeBytes: 1_500_000_000,
          seeders: 63,
          leechers: 2,
          indexerProtocol: "torznab",
          downloadUrl: "https://fixture.invalid/a.torrent"
        }]
      })
    });
  });

  await authenticateAndNavigate(page, "/search");
  await page.getByPlaceholder("Search for a release…").fill("The Simpsons S38");
  await page.getByRole("button", { name: "Search", exact: true }).click();
  await expect(page.getByText("The.Simpsons.S38E01.1080p.WEB-DL")).toBeVisible();

  await expect(page.getByText("Rejected", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /Block/ })).toHaveCount(0);

  await page.getByRole("button", { name: "Grab", exact: true }).click();
  await expect(page.getByRole("button", { name: "Send to download client" })).toBeVisible();
});

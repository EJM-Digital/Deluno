import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * A show's page against the real server and the real databases, nothing stubbed (measure 4): the Searches list narrows to one
 * episode with "Filter history to episode" and "Every episode" brings the whole history back. The show, its episodes and the
 * searches made for them are written by the smoke host's seeding route (SeriesSeedTestEndpoints, only mapped when the smoke
 * process opts in) with the repository calls the catalogue and the search planner make, since there is no metadata service
 * here to supply episodes; the page reads them back, and the menu works on them, through the real routes. The show and its
 * library are taken out again at the end, so the specs that follow meet the install as they found it.
 */
test("the Searches list narrows to one episode and Every episode brings the whole history back", async ({ page }) => {
  test.setTimeout(120_000);
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-show-history-"));
  await openScreen(page, "/tv");
  const library = await api<{ id: string }>(page, "POST", "/api/libraries", {
    name: "History shows", mediaType: "tv", purpose: "collection", rootPath: folder, importWorkflow: "copy"
  });
  expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
  const title = `History Show ${Date.now()}`;
  const seeded = await api<{ seriesId: string }>(page, "POST", "/api/testing/series/seed", {
    libraryId: library.body.id, title, year: 2024, episodeCount: 3,
    searches: [{ episodeNumber: 1, outcome: "no_match" }, { episodeNumber: 2, outcome: "no_match" }, { episodeNumber: 2, outcome: "no_match" }, { episodeNumber: 3, outcome: "no_match" }]
  });
  expect(seeded.status, JSON.stringify(seeded.body)).toBe(200);
  try {
    await page.goto(`/tv/${seeded.body.seriesId}`);
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    await page.getByRole("tab", { name: "History" }).click();
    await expect(page.getByText("Latest 4 of 4")).toBeVisible();
    const scopes = () => page.getByRole("row").filter({ hasText: "No match" }).allInnerTexts();
    expect((await scopes()).length).toBe(4);

    await page.getByRole("combobox", { name: "Filter history to episode" }).click();
    await page.getByRole("option", { name: /^S01E02/ }).click();

    await expect(page.getByText("Latest 2 of 2")).toBeVisible();
    const narrowed = await scopes();
    expect(narrowed.length).toBe(2);
    for (const row of narrowed) expect(row).toContain("S01E02");
    expect(narrowed.join(" ")).not.toContain("S01E01");
    expect(narrowed.join(" ")).not.toContain("S01E03");

    await page.getByRole("combobox", { name: "Filter history to episode" }).click();
    await page.getByRole("option", { name: "Every episode" }).click();

    await expect(page.getByText("Latest 4 of 4")).toBeVisible();
    expect((await scopes()).length).toBe(4);
  } finally {
    await api(page, "POST", "/api/series/bulk", { seriesIds: [seeded.body.seriesId], operation: "remove" });
    await api(page, "DELETE", `/api/libraries/${library.body.id}`);
    fs.rmSync(folder, { recursive: true, force: true });
  }
});

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * A show's own page against the real server, nothing stubbed (measure 4, issue 1270). The show and its three episodes are this
 * spec's own, written by the smoke host's seeding route with the same repository calls the catalogue makes (no metadata service
 * is asked); every button, switch and menu is pressed and checked against the request the server received, the state the server
 * keeps, and the words on screen.
 */

const stamp = `sd${Date.now()}`;
let folder = "";
let libraryId = "";
let seriesId = "";
const title = `Show page ${stamp}`;

interface Episode { episodeId: string; seasonNumber: number; episodeNumber: number; monitored: boolean }
interface Detail { episodes: Episode[]; monitored?: boolean }

const detail = async (page: Page) => (await api<Detail>(page, "GET", `/api/series/${seriesId}/inventory`)).body;
const lastWeek = new Date(Date.now() - 7 * 86_400_000).toISOString().slice(0, 10);

/** Every season open, whether the page remembered it open or closed. */
async function expandAll(page: Page) {
  const expand = page.getByRole("button", { name: "Expand all", exact: true });
  if (await expand.isVisible()) await expand.click();
  await expect(page.getByRole("button", { name: "Collapse all", exact: true })).toBeVisible();
}

async function openShow(page: Page) {
  await openScreen(page, "/tv");
  await page.goto(`/tv/${seriesId}`);
  await untilQuiet(page);
  await expectNoErrorScreen(page);
  await expect(page.getByRole("heading", { name: title, level: 1 })).toBeVisible();
}

test.describe("A show's page", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/tv");
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-showpage-"));
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Show page ${stamp}`, mediaType: "tv", purpose: "collection", rootPath: folder, importWorkflow: "copy" });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryId = made.body.id;
    const seeded = await api<{ seriesId: string }>(page, "POST", "/api/testing/series/seed", { libraryId, title, year: 2024, episodeCount: 3, firstAirDate: lastWeek });
    expect(seeded.status, JSON.stringify(seeded.body)).toBe(200);
    seriesId = seeded.body.seriesId;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/tv");
    await api(page, "POST", "/api/series/bulk", { seriesIds: [seriesId], operation: "remove" });
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("All TV goes back to the shelf", async ({ page }) => {
    await openShow(page);
    await page.getByRole("link", { name: "All TV", exact: true }).click();
    await expect(page).toHaveURL(/\/tv$/);
  });

  test("the shield beside the title unmonitors the show and monitors it again", async ({ page }) => {
    await openShow(page);
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Monitored — click to unmonitor" }).click();
    await expect(page.getByRole("button", { name: "Unmonitored — click to monitor" })).toBeVisible();
    await page.getByRole("button", { name: "Unmonitored — click to monitor" }).click();
    await expect(page.getByRole("button", { name: "Monitored — click to unmonitor" })).toBeVisible();
    expect(requests.find("PUT", /^\/api\/series\/monitoring$/)).toHaveLength(2);
    requests.stop();
  });

  test("Show and Hide open and close a season, Expand all and Collapse all do every season, and the episode switch reaches the server", async ({ page }) => {
    await openShow(page);
    const season = page.getByRole("button", { name: /^(Show|Hide)$/ }).first();
    if ((await season.innerText()) === "Show") await season.click();
    await expect(season).toHaveText("Hide");
    await expect(page.getByRole("switch", { name: "Monitor S01E02" })).toBeVisible();
    await season.click();
    await expect(page.getByRole("switch", { name: "Monitor S01E02" })).toHaveCount(0);
    await page.getByRole("button", { name: "Expand all", exact: true }).click();
    await expect(page.getByRole("switch", { name: "Monitor S01E02" })).toBeVisible();
    await page.getByRole("button", { name: "Collapse all", exact: true }).click();
    await expect(page.getByRole("switch", { name: "Monitor S01E02" })).toHaveCount(0);
    await page.getByRole("button", { name: "Expand all", exact: true }).click();

    const requests = recordRequests(page);
    const toggle = page.getByRole("switch", { name: "Monitor S01E02" });
    await toggle.click();
    await expect(toggle).not.toBeChecked();
    expect(requests.find("PUT", /\/api\/series\/episodes\/monitoring$/)).toHaveLength(1);
    await expect.poll(async () => (await detail(page)).episodes.find((e) => e.episodeNumber === 2)?.monitored).toBe(false);
    await toggle.click();
    await expect(toggle).toBeChecked();
    await expect.poll(async () => (await detail(page)).episodes.find((e) => e.episodeNumber === 2)?.monitored).toBe(true);
    requests.stop();
  });

  test("Monitor season switches every episode in the season off and on", async ({ page }) => {
    await openShow(page);
    const requests = recordRequests(page);
    const season = page.getByRole("switch", { name: /^Monitor Season 1$/ });
    await season.click();
    await expect(season).not.toBeChecked();
    await expect.poll(async () => (await detail(page)).episodes.every((e) => !e.monitored)).toBe(true);
    await season.click();
    await expect(season).toBeChecked();
    await expect.poll(async () => (await detail(page)).episodes.every((e) => e.monitored)).toBe(true);
    expect(requests.seen.filter((r) => r.method !== "GET" && /monitoring/.test(r.path)).length).toBe(2);
    requests.stop();
  });

  test("Search season, Search and send these, Search now and Choose a release each ask the server and the screen stays whole", async ({ page }) => {
    await openShow(page);
    await expandAll(page);
    const requests = recordRequests(page);

    await page.getByRole("button", { name: "Search season", exact: true }).first().click();
    await expect.poll(() => requests.find("POST", /\/seasons\/1\/search$/).length).toBe(1);
    await untilQuiet(page);

    await page.getByRole("button", { name: "Search and send these", exact: true }).click();
    await expect.poll(() => requests.find("POST", /\/episodes\/search$/).length).toBe(1);
    await untilQuiet(page);

    await page.getByTitle("Deluno applies the active Library Profile and sends the best acceptable release.").click();
    await expect.poll(() => requests.find("POST", new RegExp(`/api/series/${seriesId}/search$`)).length).toBe(1);
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    // The Next step card offers the same search, as its own button.
    await page.getByLabel("Next Step").getByRole("button", { name: "Search now" }).click();
    await expect.poll(() => requests.find("POST", new RegExp(`/api/series/${seriesId}/search$`)).length).toBe(2);
    await untilQuiet(page);

    await page.getByRole("button", { name: "Choose a release", exact: true }).click();
    await expect.poll(() => requests.find("POST", new RegExp(`/api/series/${seriesId}/search$`)).length).toBe(3);
    await untilQuiet(page);
    await expectNoErrorScreen(page);
    requests.stop();
  });

  test("Edit numbering opens the numbering drawer, Cancel writes nothing, and Save numbering writes the chosen model", async ({ page }) => {
    await openShow(page);
    await page.getByRole("tab", { name: "Destination", exact: true }).click();
    await page.getByRole("button", { name: "Edit numbering", exact: true }).click();
    const drawer = page.getByRole("dialog", { name: "TV numbering" });
    await expect(drawer).toBeVisible();

    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    expect(requests.find("PUT", /\/numbering$/)).toHaveLength(0);

    await page.getByRole("button", { name: "Edit numbering", exact: true }).click();
    await expect(drawer).toBeVisible();
    await drawer.getByRole("combobox", { name: "Series type" }).selectOption("anime");
    await expect(drawer.getByRole("combobox", { name: "Numbering scheme" })).toBeVisible();
    await drawer.getByRole("combobox", { name: "Mapping source" }).selectOption("owner");
    await drawer.getByRole("spinbutton", { name: "S01E01 absolute number" }).fill("11");
    await drawer.getByRole("spinbutton", { name: "S01E01 scene season" }).fill("2");
    await drawer.getByRole("spinbutton", { name: "S01E01 scene episode" }).fill("5");
    await drawer.getByRole("button", { name: "Save numbering", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    const saved = requests.find("PUT", /\/numbering$/);
    expect(saved).toHaveLength(1);
    expect(saved[0].body).toMatchObject({ seriesType: "anime", numberingSource: "owner", mappings: expect.arrayContaining([expect.objectContaining({ absoluteNumber: 11, sceneSeasonNumber: 2, sceneEpisodeNumber: 5 })]) });
    requests.stop();
    await untilQuiet(page);
  });

  test("an episode opens its drawer: its Monitor switch, Search and send the best release and Choose a release for this episode each act on that episode alone", async ({ page }) => {
    await openShow(page);
    await expandAll(page);
    await page.getByRole("row").filter({ hasText: "S01E03" }).first().click();
    const drawer = page.getByRole("dialog").filter({ hasText: "S01E03" }).last();
    await expect(drawer).toBeVisible();

    const requests = recordRequests(page);
    const monitor = drawer.getByRole("switch", { name: "Monitor this episode" });
    await monitor.click();
    await expect(monitor).not.toBeChecked();
    await expect.poll(async () => (await detail(page)).episodes.find((e) => e.episodeNumber === 3)?.monitored).toBe(false);
    await monitor.click();
    await expect(monitor).toBeChecked();

    await drawer.getByRole("button", { name: "Search and send the best release", exact: true }).click();
    await expect.poll(() => requests.find("POST", /\/episodes\/search$/).length).toBe(1);
    await untilQuiet(page);
    await drawer.getByRole("button", { name: "Choose a release for this episode", exact: true }).click();
    await expect.poll(() => requests.find("POST", /\/episodes\/search$/).length).toBe(2);
    requests.stop();
    await page.keyboard.press("Escape");
  });

  test("the Automation switches reach the server and Refresh metadata says what it could not do", async ({ page }) => {
    await openShow(page);
    await page.getByRole("tab", { name: "Destination", exact: true }).click();
    const requests = recordRequests(page);
    const automation = page.getByRole("switch", { name: "Background automation" });
    const before = await automation.isChecked();
    await automation.click();
    await expect(automation).toBeChecked({ checked: !before });
    await automation.click();
    await expect(automation).toBeChecked({ checked: before });

    const newEpisodes = page.getByRole("switch", { name: "Monitor new episodes" });
    const was = await newEpisodes.isChecked();
    await newEpisodes.click();
    await expect(newEpisodes).toBeChecked({ checked: !was });
    expect(requests.find("PUT", /\/monitor-new-episodes$/)).toHaveLength(1);
    await newEpisodes.click();
    await expect(newEpisodes).toBeChecked({ checked: was });

    await page.getByRole("tab", { name: "Episodes", exact: true }).click();
    await page.getByRole("button", { name: "Refresh metadata", exact: true }).click();
    await expect.poll(() => requests.find("POST", /\/metadata\/refresh$/).length).toBe(1);
    await untilQuiet(page);
    await expectNoErrorScreen(page);
    requests.stop();
  });

  test("Everything about this show opens Activity narrowed to that show", async ({ page }) => {
    await openShow(page);
    await page.getByRole("tab", { name: "History", exact: true }).click();
    await page.getByRole("link", { name: "Everything about this show", exact: true }).click();
    await expect(page).toHaveURL(/\/activity\?/);
    await expectNoErrorScreen(page);
  });

  test("Edit metadata opens the provider record for the show and Close shuts it", async ({ page }) => {
    await openShow(page);
    await page.getByRole("button", { name: "Edit metadata", exact: true }).click();
    const drawer = page.getByRole("dialog", { name: "Metadata" });
    await expect(drawer.getByText("Not linked").first()).toBeVisible();
    await drawer.getByRole("button", { name: "Close", exact: true }).first().click();
    await expect(drawer).toHaveCount(0);
  });

  test("Remove from Deluno asks first, Cancel keeps the show, and Remove TV show takes it from the server", async ({ page }) => {
    await openShow(page);
    await page.getByRole("button", { name: "Remove from Deluno", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: `Remove ${title}?` });
    await expect(dialog).toBeVisible();
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    expect((await api(page, "GET", `/api/series/${seriesId}/inventory`)).status).toBe(200);

    await page.getByRole("button", { name: "Remove from Deluno", exact: true }).click();
    const requests = recordRequests(page);
    await dialog.getByRole("button", { name: "Remove TV show", exact: true }).click();
    await expect(page).toHaveURL(/\/tv$/);
    requests.stop();
    expect(requests.find("POST", /^\/api\/series\/bulk$/)).toHaveLength(1);
    expect((await api(page, "GET", `/api/series/${seriesId}/inventory`)).status).toBe(404);
  });
});

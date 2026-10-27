import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Discover against the real server and the real metadata service, nothing stubbed (measure 4, issue 1270): a popular title's
 * Add button opens the drawer, the library and quality profile are chosen there, and Add sends exactly that choice to the server,
 * which then holds the film in that library. The libraries are this spec's own, in folders of their own.
 */

const stamp = `dc${Date.now()}`;
const roots: string[] = [];
const libs = { a: { id: "", name: `Discover A ${stamp}` }, b: { id: "", name: `Discover B ${stamp}` } };
const added: string[] = [];
const profileIds: string[] = [];

test.describe("Discover", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/discover");
    for (const key of ["a", "b"] as const) {
      const root = fs.mkdtempSync(path.join(os.tmpdir(), `deluno-real-discover-${key}-`));
      roots.push(root);
      const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: libs[key].name, mediaType: "movies", purpose: "collection", rootPath: root });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      libs[key].id = made.body.id;
    }
    const model = await api<{ tiers: Array<{ name: string }> }>(page, "GET", "/api/quality-model");
    const tier = model.body.tiers[0]?.name;
    expect(tier).toBeTruthy();
    for (const suffix of ["one", "two"]) {
      const profile = await api<{ id: string }>(page, "POST", "/api/quality-profiles", {
        name: `Discover profile ${suffix} ${stamp}`, mediaType: "movies", cutoffQuality: tier, allowedQualities: tier, customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false
      });
      expect(profile.status, JSON.stringify(profile.body)).toBeLessThan(300);
      profileIds.push(profile.body.id);
    }
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/discover");
    for (const id of added) await api(page, "DELETE", `/api/movies/${id}`);
    for (const lib of Object.values(libs)) if (lib.id) await api(page, "DELETE", `/api/libraries/${lib.id}`);
    for (const id of profileIds) await api(page, "DELETE", `/api/quality-profiles/${id}`);
    await page.close();
    for (const root of roots) fs.rmSync(root, { recursive: true, force: true });
  });

  test("Add opens a drawer for that title; Cancel adds nothing, and the library and quality profile chosen are what Add sends", async ({ page }) => {
    await openScreen(page, "/discover");
    await untilQuiet(page);
    await expectNoErrorScreen(page);
    let chosenProfile = "";
    const first = page.getByRole("button", { name: /^Add / }).first();
    const label = (await first.getAttribute("aria-label")) ?? "";
    const title = label.replace(/^Add /, "");
    expect(title.length).toBeGreaterThan(0);

    await first.click();
    const drawer = page.getByRole("dialog", { name: `Add ${title}` });
    await expect(drawer).toBeVisible();

    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    expect(requests.find("POST", /^\/api\/discover\/add$/)).toHaveLength(0);

    await first.click();
    await expect(drawer).toBeVisible();
    const library = drawer.getByRole("combobox", { name: "Library" });
    await library.selectOption(libs.b.id);
    const profile = drawer.getByRole("combobox", { name: "Quality profile" });
    // The library's own profile is marked, and choosing it is the same as choosing nothing; pick one of this spec's others.
    const other: string = await profile.locator("option").evaluateAll((all, ids) => (all as HTMLOptionElement[]).filter((o) => ids.includes(o.value) && !o.text.includes("library's")).map((o) => o.value)[0], profileIds);
    expect(other).toBeTruthy();
    await profile.selectOption(other);
    chosenProfile = other;
    await drawer.getByRole("switch", { name: "Search for it as soon as it is added" }).uncheck();
    await drawer.getByRole("button", { name: "Add", exact: true }).click();

    await expect.poll(() => requests.find("POST", /^\/api\/discover\/add$/).length).toBe(1);
    requests.stop();
    const sent = requests.find("POST", /^\/api\/discover\/add$/)[0].body as Record<string, unknown>;
    expect(sent.qualityProfileId).toBe(chosenProfile);
    expect(sent).toMatchObject({ title, libraryId: libs.b.id, qualityProfileId: expect.any(String), monitored: true, searchForMissingNow: false });

    const stored = await api<{ items: Array<{ id: string; title: string }> }>(page, "GET", `/api/movies/page?search=${encodeURIComponent(title)}&pageSize=20&facets=false`);
    const mine = stored.body.items.filter((m) => m.title === title);
    expect(mine.length).toBe(1);
    added.push(mine[0].id);
    const memberships = await api<Array<{ libraryId: string }>>(page, "GET", `/api/movies/${mine[0].id}/libraries/`);
    expect(memberships.body.map((m) => m.libraryId)).toEqual([libs.b.id]);
  });
});

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The add dialog on the Movies and TV shelves against the real server, nothing stubbed (measure 4, issue 1270): the IMDb field,
 * the library it is added into, Monitor it, which episodes to monitor (shows), search as soon as it is added, Cancel and the add
 * button each send, or do not send, what the server then keeps. The libraries are this spec's own, in folders of their own.
 */

const stamp = `ad${Date.now()}`;
const roots: string[] = [];
const libs = {
  movies: { id: "", name: `Add films ${stamp}` },
  moviesOther: { id: "", name: `Add other films ${stamp}` },
  tv: { id: "", name: `Add shows ${stamp}` },
  tvOther: { id: "", name: `Add other shows ${stamp}` }
};
const made: Array<{ api: string; id: string }> = [];

async function makeLibrary(page: Page, key: keyof typeof libs, mediaType: "movies" | "tv") {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-adddialog-"));
  roots.push(root);
  const reply = await api<{ id: string }>(page, "POST", "/api/libraries", { name: libs[key].name, mediaType, purpose: "collection", rootPath: root });
  expect(reply.status, JSON.stringify(reply.body)).toBeLessThan(300);
  libs[key].id = reply.body.id;
}

const variants = [
  { name: "movie", path: "/movies", button: "Add movie", manual: "Add movie manually", create: "/api/movies", list: "/api/movies/page", lib: "movies", other: "moviesOther" },
  { name: "show", path: "/tv", button: "Add TV show", manual: "Add TV show manually", create: "/api/series", list: "/api/series/page", lib: "tv", other: "tvOther" }
] as const;

test.describe("The add dialog", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    await makeLibrary(page, "movies", "movies");
    await makeLibrary(page, "moviesOther", "movies");
    await makeLibrary(page, "tv", "tv");
    await makeLibrary(page, "tvOther", "tv");
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/movies");
    for (const item of made) {
      if (item.api === "series") await api(page, "POST", "/api/series/bulk", { seriesIds: [item.id], operation: "remove" });
      else await api(page, "DELETE", `/api/${item.api}/${item.id}`);
    }
    for (const lib of Object.values(libs)) if (lib.id) await api(page, "DELETE", `/api/libraries/${lib.id}`);
    await page.close();
    for (const root of roots) fs.rmSync(root, { recursive: true, force: true });
  });

  for (const variant of variants) {
    test(`Cancel closes the ${variant.name} dialog and sends nothing`, async ({ page }) => {
      await openScreen(page, variant.path);
      await page.getByRole("button", { name: variant.button, exact: true }).first().click();
      const dialog = page.getByRole("dialog");
      await dialog.getByLabel("What do you want to add?").fill(`Never added ${stamp}`);
      const requests = recordRequests(page);
      await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(dialog).toHaveCount(0);
      requests.stop();
      expect(requests.seen.filter((r) => r.method === "POST")).toHaveLength(0);
    });

    test(`the ${variant.name} dialog adds into the library chosen, with the IMDb ID typed, Monitor it off and the search switch on, and the server keeps it that way`, async ({ page }) => {
      await openScreen(page, variant.path);
      const title = `Dialog ${variant.name} ${stamp}`;
      await page.getByRole("button", { name: variant.button, exact: true }).first().click();
      const dialog = page.getByRole("dialog");
      await dialog.getByLabel("What do you want to add?").fill(title);
      await dialog.getByText("Can’t find it? Add it manually", { exact: true }).click();
      await dialog.getByPlaceholder("IMDb ID (optional)").fill("tt0111161");
      await dialog.getByRole("combobox", { name: "Library" }).selectOption(libs[variant.other].id);
      await dialog.getByRole("checkbox", { name: "Monitor it" }).uncheck();
      await expect(dialog.getByRole("checkbox", { name: "Monitor it" })).not.toBeChecked();
      await dialog.getByRole("checkbox", { name: "Search for it as soon as it is added" }).check();
      await expect(dialog.getByRole("checkbox", { name: "Search for it as soon as it is added" })).toBeChecked();
      await dialog.getByRole("checkbox", { name: "Search for it as soon as it is added" }).uncheck();

      const requests = recordRequests(page);
      await dialog.getByRole("button", { name: variant.manual, exact: true }).click();
      await expect(dialog).toHaveCount(0);
      requests.stop();
      const posts = requests.find("POST", new RegExp(`^${variant.create}/?$`));
      expect(posts).toHaveLength(1);
      expect(posts[0].body).toMatchObject({ title, imdbId: "tt0111161", monitored: false, libraryId: libs[variant.other].id });

      const stored = await api<{ items: Array<{ id: string; title: string; monitored: boolean }> }>(page, "GET", `${variant.list}?search=${encodeURIComponent(title)}&facets=false`);
      expect(stored.body.items).toHaveLength(1);
      expect(stored.body.items[0].monitored).toBe(false);
      made.push({ api: variant.name === "movie" ? "movies" : "series", id: stored.body.items[0].id });
    });
  }

  test("a show's dialog offers which episodes to monitor, and the choice is what the server is sent", async ({ page }) => {
    await openScreen(page, "/tv");
    const title = `Dialog preset ${stamp}`;
    await page.getByRole("button", { name: "Add TV show", exact: true }).first().click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel("What do you want to add?").fill(title);
    const preset = dialog.getByRole("combobox", { name: "Which episodes to monitor" });
    await expect(preset).toBeVisible();
    const options = await preset.locator("option").evaluateAll((all) => all.map((option) => (option as HTMLOptionElement).value));
    expect(options.length).toBeGreaterThan(1);
    const chosen = options[options.length - 1];
    await preset.selectOption(chosen);
    await dialog.getByText("Can’t find it? Add it manually", { exact: true }).click();
    const requests = recordRequests(page);
    await dialog.getByRole("button", { name: "Add TV show manually", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await untilQuiet(page);
    requests.stop();
    const posts = requests.find("POST", /^\/api\/series\/?$/);
    expect(posts).toHaveLength(1);
    expect(JSON.stringify(posts[0].body)).toContain(chosen);
    const stored = await api<{ items: Array<{ id: string }> }>(page, "GET", `/api/series/page?search=${encodeURIComponent(title)}&facets=false`);
    expect(stored.body.items).toHaveLength(1);
    made.push({ api: "series", id: stored.body.items[0].id });
  });
});

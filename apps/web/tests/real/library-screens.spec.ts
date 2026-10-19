import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The Movies and TV shows library screens against the real server, nothing stubbed (measure 4, issue 1270):
 * the empty library, a library with titles seeded through the real API, the add-a-title dialog, search, the row
 * actions and removal. Each action asserts the request the server really received, the state the server and the
 * screen end in, and the words on screen.
 */

interface Variant {
  name: string;
  path: string;
  listApi: string;
  createApi: string;
  create: (title: string) => Record<string, unknown>;
  empty: string;
  emptyHelp: string;
  addButton: string;
  addManually: string;
  searchPlaceholder: string;
  removeButton: string;
}

const VARIANTS: Variant[] = [
  {
    name: "movies", path: "/movies", listApi: "/api/movies/page", createApi: "/api/movies/",
    create: (title) => ({ title, releaseYear: 2024, monitored: true }),
    empty: "Your movies library is empty", emptyHelp: "Add your first movie to start monitoring releases, running search, and building out your collection.",
    addButton: "Add movie", addManually: "Add movie manually", searchPlaceholder: "Search movies…", removeButton: "Remove movie"
  },
  {
    name: "TV shows", path: "/tv", listApi: "/api/series/page", createApi: "/api/series/",
    create: (title) => ({ title, startYear: 2024, monitored: true }),
    empty: "Your TV shows library is empty", emptyHelp: "Add your first TV show to start monitoring releases, running search, and building out your collection.",
    addButton: "Add TV show", addManually: "Add TV show manually", searchPlaceholder: "Search TV shows…", removeButton: "Remove TV show"
  }
];

type Titles = { items: Array<{ id: string; title: string }>; totalCount?: number };

/** The disposable database may hold titles other specs left: empty the library through the real API first. */
async function emptyTheLibrary(page: Page, variant: Variant) {
  const root = variant.createApi;
  for (let round = 0; round < 20; round++) {
    const list = await api<Titles>(page, "GET", `${variant.listApi}?pageSize=200&facets=false`);
    if (!list.body.items?.length) return;
    for (const item of list.body.items) await api(page, "DELETE", `${root}${item.id}`);
  }
}

const search = (page: Page, variant: Variant) => page.getByPlaceholder(variant.searchPlaceholder);

for (const variant of VARIANTS) {
  test.describe(`${variant.name} library`, () => {
    test.describe.configure({ mode: "serial" });

    test(`an empty ${variant.name} library says so in words and offers to add the first title`, async ({ page }) => {
      await openScreen(page, variant.path);
      await emptyTheLibrary(page, variant);
      await page.reload();
      await untilQuiet(page);
      await expectNoErrorScreen(page);

      await expect(page.getByText(variant.empty, { exact: true })).toBeVisible();
      await expect(page.getByText(variant.emptyHelp, { exact: true })).toBeVisible();
      await expect(page.getByRole("button", { name: variant.addButton, exact: true }).first()).toBeVisible();
      // The server agrees the library is empty.
      const list = await api<Titles>(page, "GET", `${variant.listApi}?pageSize=50&facets=false`);
      expect(list.status).toBe(200);
      expect(list.body.items).toEqual([]);
    });

    test(`the add dialog sends the title and year the person typed, and the new ${variant.name} title appears`, async ({ page }) => {
      await openScreen(page, variant.path);
      const title = `Real add ${variant.name} ${Date.now()}`;
      const requests = recordRequests(page);

      await page.getByRole("button", { name: variant.addButton, exact: true }).first().click();
      const dialog = page.getByRole("dialog");
      await expect(dialog.getByText(variant.addButton, { exact: true }).first()).toBeVisible();
      await dialog.getByLabel("What do you want to add?").fill(title);
      await dialog.getByText("Can’t find it? Add it manually", { exact: true }).click();
      await dialog.getByPlaceholder(/Year \(optional\)|Start year \(optional\)/).fill("2021");
      await dialog.getByRole("button", { name: variant.addManually, exact: true }).click();

      await expect(dialog).toHaveCount(0);
      await expect(page.getByText(title, { exact: true }).first()).toBeVisible();
      requests.stop();

      const posts = requests.find("POST", new RegExp(`^${variant.createApi.replace(/\/$/, "")}/?$`));
      expect(posts, "the screen sent exactly one create request").toHaveLength(1);
      expect(posts[0].body).toMatchObject({ title });
      expect(JSON.stringify(posts[0].body)).toContain("2021");

      const stored = await api<Titles>(page, "GET", `${variant.listApi}?search=${encodeURIComponent(title)}&facets=false`);
      expect(stored.body.items.map((item) => item.title)).toEqual([title]);
    });

    test(`search narrows ${variant.name} through a real request, and a search that matches nothing says so`, async ({ page }) => {
      await openScreen(page, variant.path);
      const stamp = Date.now();
      const keep = `Real search keep ${stamp}`;
      const other = `Real search other ${stamp}`;
      await api(page, "POST", variant.createApi, variant.create(keep));
      await api(page, "POST", variant.createApi, variant.create(other));
      await page.reload();
      await untilQuiet(page);
      await expect(page.getByText(keep, { exact: true })).toBeVisible();
      await expect(page.getByText(other, { exact: true })).toBeVisible();

      const requests = recordRequests(page);
      const narrowed = page.waitForResponse((r) => r.url().includes(variant.listApi) && new URL(r.url()).searchParams.get("search") === keep);
      await search(page, variant).fill(keep);
      expect((await narrowed).ok()).toBe(true);
      await expect(page.getByText(keep, { exact: true })).toBeVisible();
      await expect(page.getByText(other, { exact: true })).toHaveCount(0);
      expect(requests.find("GET", new RegExp(variant.listApi.replace(/\//g, "\\/"))).some((r) => new URLSearchParams(r.search).get("search") === keep)).toBe(true);

      const nothing = `zz-no-such-title-${stamp}`;
      await search(page, variant).fill(nothing);
      await expect(page.getByText("Nothing matches", { exact: true })).toBeVisible();
      await expect(page.getByText(/Try clearing filters or broadening your search\. Your library has \d+ total titles?\./)).toBeVisible();
      await page.getByRole("button", { name: "Clear filters", exact: true }).click();
      await expect(page.getByText(keep, { exact: true })).toBeVisible();
      requests.stop();
    });

    test(`a row's Search now sends one real search for that title, and Refresh metadata one refresh, each saying what it did`, async ({ page }) => {
      await openScreen(page, variant.path);
      const title = `Real row search ${variant.name} ${Date.now()}`;
      const created = await api<{ id: string }>(page, "POST", variant.createApi, variant.create(title));
      expect(created.status).toBe(201);
      await page.reload();
      await untilQuiet(page);

      const requests = recordRequests(page);
      await page.getByRole("button", { name: `Search for ${title} now`, exact: true }).click();
      const result = page.getByLabel(`Result for ${title}`);
      await expect(result).toBeVisible();
      await expect(result).toHaveText("Nothing found yet");
      expect(requests.seen.filter((r) => r.method === "POST").map((r) => r.path)).toEqual([`${variant.createApi}${created.body.id}/search`]);

      await page.getByRole("button", { name: `Refresh metadata for ${title}`, exact: true }).click();
      await expect.poll(() => requests.seen.filter((r) => r.method === "POST").length).toBe(2);
      await expect(result).toBeVisible();
      // This host has no metadata provider key, so the real answer to a refresh is the failure line.
      await expect(result).toHaveText("Refresh failed");
      requests.stop();
      expect(requests.seen.filter((r) => r.method === "POST").map((r) => r.path)[1]).toBe(`${variant.createApi}${created.body.id}/metadata/refresh`);
      await expectNoErrorScreen(page);
    });

    test(`selecting a title and choosing Remove asks first, sends one remove request, and the title is gone from the server`, async ({ page }) => {
      await openScreen(page, variant.path);
      const title = `Real remove ${variant.name} ${Date.now()}`;
      const created = await api<{ id: string }>(page, "POST", variant.createApi, variant.create(title));
      await page.reload();
      await untilQuiet(page);

      await page.getByRole("button", { name: `Select ${title}`, exact: true }).click();
      await expect(page.getByRole("button", { name: `Deselect ${title}`, exact: true })).toBeVisible();
      await page.getByRole("button", { name: "Remove", exact: true }).click();
      const dialog = page.getByRole("dialog");
      await expect(dialog).toContainText("Imported files are left alone.");

      // Cancel keeps the title.
      await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(dialog).toHaveCount(0);
      expect((await api(page, "GET", `${variant.createApi}${created.body.id}`)).status).toBe(200);

      await page.getByRole("button", { name: "Remove", exact: true }).click();
      const requests = recordRequests(page);
      await page.getByRole("dialog").getByRole("button", { name: variant.removeButton, exact: true }).click();
      await expect(page.getByText(title, { exact: true })).toHaveCount(0);
      requests.stop();
      const removes = requests.seen.filter((r) => r.method === "POST" && r.path === `${variant.createApi}bulk`);
      expect(removes, JSON.stringify(requests.seen)).toHaveLength(1);
      expect(removes[0].body).toMatchObject({ operation: "remove" });
      expect(JSON.stringify(removes[0].body)).toContain(created.body.id);
      expect((await api(page, "GET", `${variant.createApi}${created.body.id}`)).status).toBe(404);
    });
  });
}

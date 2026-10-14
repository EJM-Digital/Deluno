import { expect, test, type Page, type Response } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { untilQuiet } from "../helpers/settle";

/**
 * #1048: every title is in exactly one pill, clicking a pill never changes
 * another number, and the toolbar never moves.
 *
 * Clicks every pill on the Movies shelf at 390, 1669 and 1920 wide and asserts,
 * for each click:
 *  - the search bar and the toolbar keep exactly the same box;
 *  - All, and every other pill's number, are unchanged;
 *  - the pill's number is what the list returned (the page's own total), and
 *    the seeded titles it renders are that pill's monitored seeded titles.
 *
 * The library is small on purpose (every title renders, no virtualisation
 * sampling) and is seeded through the same test-only endpoint the scale spec
 * uses, then removed again so later specs see an empty shelf.
 */

const SEED_COUNT = 40;
const TITLE_PREFIX = "Smoke Scale Film";

const PILLS = [
  { bucket: "covered", key: "covered", label: /^Quality met/ },
  { bucket: "upgrade", key: "upgrades", label: /^Upgradable/ },
  { bucket: "downloading", key: "downloading", label: /^Downloading/ },
  { bucket: "missing", key: "missing", label: /^Missing/ },
  { bucket: "upcoming", key: "upcoming", label: /^Upcoming/ }
] as const;

interface SeedResult {
  totalCreated: number;
  perBucketMonitored: Record<string, number>;
  unmonitoredCount: number;
}

interface CataloguePageResponse {
  totalCount: number | null;
  facets: Record<string, number> | null;
}

async function boxes(page: Page) {
  const search = page.getByPlaceholder(/^Search /).first();
  const toolbar = page.locator("div.sticky").filter({ has: search }).first();
  return {
    search: await search.boundingBox(),
    toolbar: await toolbar.boundingBox()
  };
}

/** Every seeded title on the shelf, scrolling through it so a virtualised grid cannot hide any. */
async function seededTitlesOnShelf(page: Page): Promise<Set<string>> {
  const container = page.locator("div.overflow-auto").first();
  const seen = new Set<string>();
  await container.evaluate((element) => { element.scrollTop = 0; });
  for (let step = 0; step < 40; step += 1) {
    for (const title of await page.locator(`p[title^="${TITLE_PREFIX}"]`).evaluateAll((nodes) => nodes.map((node) => node.getAttribute("title") ?? ""))) {
      seen.add(title);
    }
    const atEnd = await container.evaluate((element) => {
      const before = element.scrollTop;
      element.scrollTop = before + Math.max(200, element.clientHeight * 0.6);
      return element.scrollTop === before;
    });
    if (atEnd) break;
    await untilQuiet(page, { frames: 8 });
  }
  return seen;
}

function pillNumber(text: string): number {
  const match = /(\d+)\s*$/.exec(text.trim());
  return match ? Number(match[1]) : Number.NaN;
}

async function pillNumbers(page: Page): Promise<Record<string, number>> {
  const numbers: Record<string, number> = {};
  const all = page.getByRole("button", { name: /^All/ }).first();
  numbers.all = pillNumber((await all.textContent()) ?? "");
  for (const pill of PILLS) {
    numbers[pill.key] = pillNumber((await page.getByRole("button", { name: pill.label }).first().textContent()) ?? "");
  }
  numbers.unmonitored = pillNumber((await page.getByRole("button", { name: /Unmonitored/ }).first().textContent()) ?? "");
  return numbers;
}

test.describe("library pills partition the shelf and never move it (#1048)", () => {
  let seed: SeedResult;
  let headers: Record<string, string>;

  test.beforeAll(async ({ request }) => {
    // The seeding request below is given 120 s; the hook must be given at
    // least that, or a busy machine ends the whole file on the hook's own
    // 30 s default while the host is still inserting (#1057).
    test.setTimeout(150_000);
    const login = await request.post("/api/auth/login", {
      data: { username: "e2e-test-user", password: "e2e-test-password-123" }
    });
    let token: string;
    if (login.ok()) {
      token = ((await login.json()) as { accessToken: string }).accessToken;
    } else {
      const bootstrap = await request.post("/api/auth/bootstrap", {
        data: { displayName: "E2E Test User", username: "e2e-test-user", password: "e2e-test-password-123" }
      });
      expect(bootstrap.ok(), `Could not bootstrap the smoke account: HTTP ${bootstrap.status()}`).toBe(true);
      token = ((await bootstrap.json()) as { accessToken: string }).accessToken;
    }
    headers = { Authorization: `Bearer ${token}` };

    const seedResponse = await request.post("/api/testing/movies/seed-large-library", {
      headers,
      data: { count: SEED_COUNT },
      timeout: 120_000
    });
    expect(seedResponse.ok(), `Seeding failed: HTTP ${seedResponse.status()}`).toBe(true);
    seed = (await seedResponse.json()) as SeedResult;
  });

  test.afterAll(async ({ request }) => {
    test.setTimeout(150_000);
    if (!headers) return;
    const unseed = await request.post("/api/testing/movies/unseed-large-library", { headers, timeout: 120_000 });
    expect(unseed.ok(), `Cleaning up failed: HTTP ${unseed.status()}`).toBe(true);
  });

  for (const width of [1669, 1920]) {
    test(`the search box is the same width on Movies and TV Shows at ${width}px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      // The search box shares its row with the pills, whose numbers arrive
      // with the first page. Measuring before they land compares a half-drawn
      // toolbar with a finished one (#1057), so wait for the numbers first.
      const settledSearchBox = async () => {
        await expect(page.getByRole("button", { name: /^All\s+\d+/ }).first()).toBeVisible({ timeout: 15_000 });
        return page.getByPlaceholder(/^Search /).first().boundingBox();
      };
      await authenticateAndNavigate(page, "/movies");
      const movies = await settledSearchBox();
      await authenticateAndNavigate(page, "/tv");
      const tv = await settledSearchBox();
      expect(movies).not.toBeNull();
      expect(tv).not.toBeNull();
      // Sub-pixel layout rounding is not a different width; a real defect is
      // tens of pixels.
      expect(Math.abs(tv!.width - movies!.width), "TV Shows draws the search box at a different width from Movies").toBeLessThan(1);
    });
  }

  test("an unmonitored title with no file reads Unmonitored on its card, never Missing", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 900 });
    await authenticateAndNavigate(page, "/movies");
    await expect(page.locator(`p[title^="${TITLE_PREFIX}"]`).first()).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: /Unmonitored/ }).first().click();
    await untilQuiet(page);

    const container = page.locator("div.overflow-auto").first();
    await container.evaluate((element) => { element.scrollTop = 0; });
    let unmonitoredStrips = 0;
    const words: string[] = [];
    for (let step = 0; step < 40; step += 1) {
      unmonitoredStrips = Math.max(unmonitoredStrips, await page.locator('[role="img"][aria-label="Unmonitored"]').count());
      words.push(...await page.locator('[role="img"]').evaluateAll((nodes) => nodes.map((node) => node.getAttribute("aria-label") ?? "")));
      const atEnd = await container.evaluate((element) => {
        const before = element.scrollTop;
        element.scrollTop = before + Math.max(200, element.clientHeight * 0.6);
        return element.scrollTop === before;
      });
      if (atEnd) break;
      await untilQuiet(page, { frames: 8 });
    }
    expect(unmonitoredStrips, "no unmonitored title without a file said Unmonitored").toBeGreaterThan(0);
    expect(words.filter((word) => /^Missing/.test(word) || /^Subtitles missing/i.test(word)), "an unmonitored card still says Missing").toEqual([]);
  });

  for (const width of [390, 1669, 1920]) {
    test(`clicking every pill at ${width}px moves nothing and matches its number`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await authenticateAndNavigate(page, "/movies");
      await expect(page.locator(`p[title^="${TITLE_PREFIX}"]`).first()).toBeVisible({ timeout: 10_000 });

      const before = await boxes(page);
      const baseline = await pillNumbers(page);
      expect(before.search).not.toBeNull();
      expect(before.toolbar).not.toBeNull();

      // Each title is in exactly one pill, so All is the sum of the pills.
      const sum = baseline.covered + baseline.upgrades + baseline.downloading + baseline.missing + baseline.upcoming + baseline.unmonitored;
      expect(baseline.all, `All is ${baseline.all} but the pills sum to ${sum}`).toBe(sum);

      const clicks = [
        ...PILLS.map((pill) => ({ name: pill.key, locator: page.getByRole("button", { name: pill.label }).first(), bucket: pill.bucket })),
        { name: "unmonitored", locator: page.getByRole("button", { name: /Unmonitored/ }).first(), bucket: null },
        { name: "all", locator: page.getByRole("button", { name: /^All/ }).first(), bucket: null }
      ];

      for (const click of clicks) {
        const responsePromise = page.waitForResponse(
          (response: Response) => response.url().includes("/api/movies/page") && response.request().method() === "GET"
        );
        await click.locator.click();
        const response = await responsePromise;
        const body = (await response.json()) as CataloguePageResponse;
        // Wait for the shelf to show this pill's rows, not for a fixed time:
        // the response arriving is not the page having painted it, and on a
        // busy machine the gap between the two is what made this flaky (#1057).
        await expect(click.locator).toHaveAttribute("aria-pressed", "true");
        if (click.bucket) {
          await expect(page.locator(`p[title^="${TITLE_PREFIX} ${click.bucket} "]`).first()).toBeVisible({ timeout: 15_000 });
        } else {
          await expect(page.locator(`p[title^="${TITLE_PREFIX}"]`).first()).toBeVisible({ timeout: 15_000 });
        }
        await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));

        const after = await boxes(page);
        expect(after.search, `${click.name}: the search bar moved or resized`).toEqual(before.search);
        expect(after.toolbar, `${click.name}: the toolbar moved or resized`).toEqual(before.toolbar);

        expect(await pillNumbers(page), `${click.name}: a number changed when it was clicked`).toEqual(baseline);

        if (click.name !== "all") {
          const number = baseline[click.name];
          expect(body.totalCount, `${click.name}: the list total is not the pill number`).toBe(number);
        } else {
          expect(body.totalCount).toBe(baseline.all);
        }

        // The list the page returned is exactly the pill's number.
        const listed = (body as unknown as { items: unknown[] }).items.length;
        expect(listed, `${click.name}: the list length is not the pill number`).toBe(body.totalCount);

        if (click.bucket) {
          const seen = await seededTitlesOnShelf(page);
          expect(seen.size, `${click.name}: the shelf is not the monitored titles of that pill`).toBe(seed.perBucketMonitored[click.bucket]);
          for (const title of seen) {
            expect(title, `${click.name}: a title from another pill is on the shelf`).toContain(`${TITLE_PREFIX} ${click.bucket} `);
          }
        } else if (click.name === "unmonitored") {
          const seen = await seededTitlesOnShelf(page);
          expect(seen.size, "unmonitored: the shelf is not the unmonitored titles").toBe(seed.unmonitoredCount);
        }
      }
    });
  }
});

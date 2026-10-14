import { expect, test, type Page, type Response } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { untilQuiet } from "../helpers/settle";

/**
 * #934 — "checked at scale": a smoke server seeded with a large synthetic
 * library (thousands of films, several pages) still shows all titles for
 * each status chip, with the first page appearing promptly.
 *
 * Every earlier #934 spec (chip-switch-no-ghosts.spec.ts) proves the fix
 * against a handful of fixture rows. That is the right size for proving a
 * race is gone, and the wrong size for proving the chip row still works once
 * a real library's shelf holds thousands rather than a dozen — a debounce
 * bug and a query-plan bug look identical at eleven rows and very different
 * at three thousand.
 *
 * The library is seeded through a test-only endpoint
 * (`POST /api/testing/movies/seed-large-library`, see
 * `MoviesLargeLibrarySeedEndpoints` in Deluno.Movies) that writes the
 * catalogue and wanted-state rows directly rather than through the real
 * add-movie endpoint, so seeding three thousand titles costs one HTTP call
 * and no job-queue noise. That endpoint only exists in this smoke process —
 * playwright.config.ts sets `Testing__EnableLargeLibrarySeedEndpoint` on the
 * backend it starts for this suite, and nowhere else.
 *
 * **Sampling, not full traversal.** The background fill (`FILL_PAGE_SIZE` in
 * library-view.tsx) loads an entire filtered result set into the browser
 * behind the scenes, but the grid is virtualised: only the rows near the
 * viewport are ever in the DOM. Rather than scroll every one of ~600 rows a
 * bucket holds, each chip is checked at the top, the middle and the bottom of
 * its shelf — enough to catch a query that quietly stops at a page boundary,
 * a sort that reorders on scroll, or a leak from a neighbouring filter,
 * without a five-minute test run.
 */

const SEED_COUNT = 3000;
const TITLE_PREFIX = "Smoke Scale Film";

/** The five states a movie can be seeded into — see WantedStatuses.cs. */
const STATUS_BUCKETS = ["missing", "upgrade", "covered", "downloading", "upcoming"] as const;

/** The quick-filter chip key and visible label for each seeded status. */
const STATUS_CHIPS: Array<{ bucket: (typeof STATUS_BUCKETS)[number]; chipKey: string; labelPattern: RegExp }> = [
  { bucket: "covered", chipKey: "covered", labelPattern: /^Quality met/ },
  { bucket: "upgrade", chipKey: "upgrades", labelPattern: /^Upgradable/ },
  { bucket: "downloading", chipKey: "downloading", labelPattern: /^Downloading/ },
  { bucket: "missing", chipKey: "missing", labelPattern: /^Missing/ },
  { bucket: "upcoming", chipKey: "upcoming", labelPattern: /^Upcoming/ }
];

interface SeedResult {
  totalCreated: number;
  libraryId: string;
  titlePrefix: string;
  perBucket: Record<string, number>;
  /**
   * The monitored half of each bucket. #1032 merged the Wanted shortcut into
   * the Missing chip: clicking it sets `monitored=true` alongside
   * `status=missing`, so the Missing chip's own facet — and the rows it shows
   * — only ever count the monitored half of the seeded "missing" bucket. Every
   * other chip leaves monitoring at "any", so `perBucket` is still the right
   * floor for them.
   */
  perBucketMonitored: Record<string, number>;
  monitoredCount: number;
  unmonitoredCount: number;
}

interface CataloguePageResponse {
  totalCount: number | null;
  facets: Record<string, number> | null;
}

function scrollContainer(page: Page) {
  // The virtualised shelf's own scroll box — see library-grid.tsx's
  // `ProgressiveGrid`. Not `overflow-x-auto` (the chip row, above it): the
  // full class name is matched, so the two never collide.
  return page.locator("div.overflow-auto").first();
}

/** Scrolls to the bottom, waiting until nothing new renders (the background fill has caught up), capped so a giant bucket cannot hang the run. */
async function scrollToSettledBottom(page: Page, maxIterations = 12) {
  const container = scrollContainer(page);
  let previousHeight = -1;
  for (let iteration = 0; iteration < maxIterations; iteration += 1) {
    const height = await container.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
      return element.scrollHeight;
    });
    if (height === previousHeight) {
      return;
    }
    previousHeight = height;
    await untilQuiet(page, { frames: 15 });
  }
}

async function scrollToMiddle(page: Page) {
  const container = scrollContainer(page);
  await container.evaluate((element) => {
    element.scrollTop = element.scrollHeight / 2;
  });
  await untilQuiet(page, { frames: 10 });
}

async function scrollToTop(page: Page) {
  const container = scrollContainer(page);
  await container.evaluate((element) => {
    element.scrollTop = 0;
  });
  await untilQuiet(page, { frames: 10 });
}

/** Every seeded title visible right now, however deep it scrolled to get there. */
async function visibleSeededTitles(page: Page): Promise<string[]> {
  const nodes = await page.locator(`p[title^="${TITLE_PREFIX}"]`).allTextContents();
  return nodes;
}

function bucketOf(title: string): string {
  // "Smoke Scale Film <bucket> <00042>" — the bucket is the fourth word.
  const parts = title.split(" ");
  return parts[3] ?? "";
}

test.describe("large library status chips at scale (#934)", () => {
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
    // The fallback account may not exist yet on a brand-new smoke database;
    // authenticateAndNavigate's own sign-in path (used per-test below) will
    // bootstrap it. Seeding needs a token too, so bootstrap here if needed.
    let token: string;
    if (login.ok()) {
      token = ((await login.json()) as { accessToken: string }).accessToken;
    } else {
      const bootstrap = await request.post("/api/auth/bootstrap", {
        data: {
          displayName: "E2E Test User",
          username: "e2e-test-user",
          password: "e2e-test-password-123"
        }
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
    expect(
      seedResponse.ok(),
      `Seeding the large library failed: HTTP ${seedResponse.status()}. Is Testing__EnableLargeLibrarySeedEndpoint set for the smoke backend?`
    ).toBe(true);
    seed = (await seedResponse.json()) as SeedResult;
    expect(seed.totalCreated).toBe(SEED_COUNT);
  });

  // The smoke suite shares one database and one worker across every spec
  // (playwright.config.ts's own comment on why), so three thousand titles
  // left behind would change what "the library is empty" means for every
  // spec that happens to run after this one — which is exactly what broke
  // workstation-workflow.spec.ts's dashboard empty-state checks the first
  // time this spec ran without cleaning up after itself. Unseeding is its
  // own test-only endpoint rather than the real bulk-remove one: that path
  // runs TitleRemovalService per title, which would leave three thousand
  // activity-feed rows behind — noise the very next spec would have to
  // explain, not the improvement being proven here.
  test.afterAll(async ({ request }) => {
    test.setTimeout(150_000);
    if (!headers) return;
    const unseedResponse = await request.post("/api/testing/movies/unseed-large-library", {
      headers,
      timeout: 120_000
    });
    expect(unseedResponse.ok(), `Cleaning up the large library failed: HTTP ${unseedResponse.status()}`).toBe(true);
  });

  test("each status chip shows all its titles, matches its own count, and never leaks another filter's rows", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");

    const timings: Record<string, number> = {};

    for (const { bucket, chipKey, labelPattern } of STATUS_CHIPS) {
      const chip = page.getByRole("button", { name: labelPattern, exact: false }).first();

      const started = Date.now();
      const responsePromise = page.waitForResponse(
        (response: Response) => response.url().includes("/api/movies/page") && response.url().includes(`status=${chipKey}`)
      );
      await chip.click();
      const response = await responsePromise;
      const body = (await response.json()) as CataloguePageResponse;

      // First page appears promptly. A real DOM assertion, not just "the
      // request finished" — Playwright's own timeout below is the "under 2s"
      // bar. Not a specific index: the default sort is Added, newest first,
      // and every seeded title in a bucket was added a moment after the one
      // before it, so the top of the shelf is the highest index, not "00000".
      const firstTitle = page.locator(`p[title^="${TITLE_PREFIX} ${bucket} "]`).first();
      //
      // This is a correctness wait, not a stopwatch: a hard 2 s bar failed
      // whenever the gate ran other suites beside it (#1057). The latency
      // is still logged below, and the ghost window itself is proved without
      // relying on wall-clock speed in chip-switch-no-ghosts.spec.ts.
      await expect(firstTitle).toBeVisible({ timeout: 15_000 });
      timings[bucket] = Date.now() - started;

      // The chip's own printed count is the API's facet total for this
      // filter, read off the very response that painted the shelf — not a
      // number this test invented, so it cannot drift from what the query
      // actually decided.
      const facetTotal = body.facets?.[chipKey] ?? -1;
      expect(facetTotal, `No ${chipKey} facet on the /api/movies/page response`).toBeGreaterThan(0);
      await expect(chip).toContainText(String(facetTotal));

      // Every seeded title carrying this bucket's tag has to be reachable
      // among the ones this test actually created — a smoke database can
      // hold rows earlier specs left behind, so the floor is the bucket's
      // own seeded count, not the seeded count exactly.
      //
      // Missing is the one chip that narrows the shelf on a second axis too:
      // #1032 made clicking it also set `monitored=true` (it absorbed the old
      // Wanted shortcut), so its facet — and the rows the assertions below
      // find — only ever cover the monitored half of the seeded bucket. Every
      // other chip leaves monitoring at "any" and still counts the whole
      // bucket — including whichever chip runs right after Missing in this
      // loop: leaving Missing for a different status resets the Monitored
      // filter Missing turned on (library-control-rail.tsx's
      // `monitoringForcedByMissing`), so the very next iteration below is
      // proof that reset actually happens rather than something this test
      // has to do by hand.
      //
      // #1048: every status pill now counts monitored titles only (an
      // unmonitored title is under Unmonitored and nowhere else), so the floor
      // is the monitored half of the bucket for every chip, not just Missing.
      const floor = seed.perBucketMonitored[bucket];
      expect(facetTotal).toBeGreaterThanOrEqual(floor);

      // Sample top, middle and bottom rather than walking every row (see the
      // file header). At every sample: nothing from a different bucket may be
      // on screen while this chip is the only one selected, and — at the
      // bottom, once the background fill has settled — the shelf still has
      // titles on it, proving the far end of a ~600-row bucket is reachable
      // rather than the query silently stopping at the first page.
      for (const moveTo of [scrollToTop, scrollToMiddle, scrollToSettledBottom]) {
        await moveTo(page);
        const visible = await visibleSeededTitles(page);
        expect(visible.length, `No seeded titles rendered for "${chipKey}" after scrolling`).toBeGreaterThan(0);
        const foreign = visible.filter((title) => {
          const seenBucket = bucketOf(title);
          return STATUS_BUCKETS.includes(seenBucket as (typeof STATUS_BUCKETS)[number]) && seenBucket !== bucket;
        });
        expect(foreign, `Titles from another filter appeared while "${chipKey}" was selected: ${foreign.join(", ")}`).toHaveLength(0);
      }
    }

    console.log("First-page timings by status chip (ms):", timings);
  });

  test("Unmonitored and Monitored show all their titles without mixing the two", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");

    // Start from a clean quick filter so the monitoring axis is the only
    // thing narrowing the shelf (library-filters.ts: the two axes multiply,
    // so leaving a status chip selected would test both at once).
    await page.getByRole("button", { name: /^All /, exact: false }).first().click();

    // Unmonitored is a chip on the rail itself (library-control-rail.tsx).
    {
      const chip = page.getByRole("button", { name: /Unmonitored/ });
      const responsePromise = page.waitForResponse(
        (response: Response) => response.url().includes("/api/movies/page") && response.url().includes("monitored=false")
      );
      const started = Date.now();
      await chip.click();
      const response = await responsePromise;
      const body = (await response.json()) as CataloguePageResponse;

      await expect(page.locator(`p[title^="${TITLE_PREFIX}"]`).first()).toBeVisible({ timeout: 2_000 });
      expect(Date.now() - started).toBeLessThan(2_000);

      const facetTotal = body.facets?.unmonitored ?? -1;
      expect(facetTotal).toBeGreaterThanOrEqual(seed.unmonitoredCount);
      await expect(chip).toContainText(String(facetTotal));

      await scrollToSettledBottom(page);
      const visible = await visibleSeededTitles(page);
      expect(visible.length, "Unmonitored shelf never rendered any seeded titles").toBeGreaterThan(0);
    }

    // Monitored has no chip of its own — it lives behind the Filter drawer's
    // Monitoring select (library-filter-panel.tsx), beside every other
    // narrowing condition.
    {
      await page.getByRole("button", { name: /Unmonitored/ }).click(); // toggle back off
      await page.getByRole("button", { name: /^Filter/ }).click();
      const started = Date.now();

      // Keyboard rather than a pointer click on the option: MenuSelect
      // portals its listbox to document.body and positions it "fixed" from
      // the trigger's own rect, and inside an open drawer that sits close
      // enough to the panel underneath it that Playwright's hit-test can
      // land on the drawer's own content instead of the popup above it.
      // Opening lands focus on the current choice ("Any monitoring", index
      // 0) — see MenuSelect's own comment on that effect — so one ArrowDown
      // reaches "Monitored" and Enter is a native click on a focused button.
      await page.getByRole("combobox", { name: "Monitoring" }).click();
      await expect(page.getByRole("option", { name: /^Monitored /, exact: false })).toBeVisible();
      await page.keyboard.press("ArrowDown");
      await page.keyboard.press("Enter");

      await expect(page.locator(`p[title^="${TITLE_PREFIX}"]`).first()).toBeVisible({ timeout: 2_000 });
      expect(Date.now() - started).toBeLessThan(2_000);

      // Fetched directly rather than sniffed off the UI's own request: the
      // request the drawer's keyboard-driven select fires is exactly the same
      // one buildCatalogueParams always builds (`monitored=true`, no status),
      // so asking for it here is the same query with none of the timing risk
      // of racing Playwright's own network listener against a fast local API.
      const monitoredResponse = await page.request.get("/api/movies/page?pageSize=1&monitored=true", { headers });
      expect(monitoredResponse.ok()).toBe(true);
      const body = (await monitoredResponse.json()) as CataloguePageResponse;

      const facetTotal = body.facets?.monitored ?? -1;
      expect(facetTotal).toBeGreaterThanOrEqual(seed.monitoredCount);

      // Close the Filter drawer before scrolling the shelf underneath it.
      // Not by clicking its toggle again: the drawer is a Radix dialog, which
      // marks the rest of the page aria-hidden while open, so the toolbar's
      // own "Filter" button is unreachable by role until the dialog closes —
      // Escape is what Radix's own dialog already wires up.
      await page.keyboard.press("Escape");
      await expect(page.getByRole("dialog", { name: "Filter" })).toHaveCount(0);
      await scrollToSettledBottom(page);
      const visible = await visibleSeededTitles(page);
      expect(visible.length, "Monitored shelf never rendered any seeded titles").toBeGreaterThan(0);
    }
  });
});

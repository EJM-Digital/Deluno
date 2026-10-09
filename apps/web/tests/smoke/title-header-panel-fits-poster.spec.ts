import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { waitForLayoutCalm } from "../helpers/layout-calm";

// #1058. The Ratings & IDs panel is never taller than the poster: the poster sets
// the header height. Every action is a visible button, two to a row.

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

// These titles must not outlive the test: the dashboard specs that run later in
// the same shared database assume an empty library (#1057).
const seeded: { movieId: string; seriesId: string }[] = [];

test.afterEach(async ({ page }) => {
  const headers = await authHeaders(page);
  for (const { movieId, seriesId } of seeded.splice(0)) {
    await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [movieId], operation: "remove" } });
    await page.request.post("/api/series/bulk", { headers, data: { seriesIds: [seriesId], operation: "remove" } });
  }
});

async function seedTitles(page: Page) {
  await authenticateAndNavigate(page, "/movies");
  const headers = await authHeaders(page);
  const stamp = Date.now();
  const movie = await (await page.request.post("/api/movies/", {
    headers,
    data: { title: `Header fit movie ${stamp}`, releaseYear: 1962, monitored: true }
  })).json() as { id: string };
  const series = await (await page.request.post("/api/series/", {
    headers,
    data: { title: `Header fit show ${stamp}`, startYear: 2024, monitored: true }
  })).json() as { id: string };
  seeded.push({ movieId: movie.id, seriesId: series.id });
  return { movie: `/movies/${movie.id}`, show: `/tv/${series.id}` };
}

const pages = [
  { kind: "movie", buttons: ["Refresh metadata", "Rescan the folder", "Preview the folder rename", "Edit metadata", "Edit movie", "Remove from Deluno"] },
  { kind: "show", buttons: ["Refresh metadata", "Rescan the folder", "Preview the folder rename", "Edit metadata", "Remove from Deluno"] }
] as const;

for (const size of [{ width: 1280, height: 900 }, { width: 1669, height: 1000 }, { width: 1920, height: 1080 }, { width: 2560, height: 1300 }]) {
  for (const spec of pages) {
    test(`the ${spec.kind} Ratings & IDs panel is no taller than its poster at ${size.width}px`, async ({ page }) => {
      const urls = await seedTitles(page);
      await page.setViewportSize(size);
      await page.goto(spec.kind === "movie" ? urls.movie : urls.show);
      const panel = page.locator("aside").filter({ hasText: "Ratings & IDs" });
      await expect(panel).toBeVisible();

      const poster = page.getByAltText(/ poster$/).or(page.getByText("No artwork yet")).first();
      const posterBox = await poster.boundingBox();
      const panelBox = await panel.boundingBox();
      expect(posterBox).not.toBeNull();
      expect(panelBox).not.toBeNull();
      console.log(`${spec.kind} ${size.width}px poster=${posterBox!.height} panel=${panelBox!.height}`);
      // The two-column layout (poster beside details) has no panel column below xl (1280px).
      if (size.width >= 1280) expect(panelBox!.height).toBeLessThanOrEqual(posterBox!.height);

      // Every button stays visible and never overlaps. The page is live (#1179): a title just added gets its
      // metadata a moment later and the header lays itself out again, so the buttons are measured together, in
      // one snapshot, until two snapshots agree, rather than one by one across that change.
      for (const name of spec.buttons) {
        await expect(panel.getByRole("button", { name: new RegExp(`^${name}`) })).toBeVisible();
      }
      const overlapping = () => panel.evaluate((element, names) => {
        const buttons = [...element.querySelectorAll("button")];
        const boxes = names.map((name) => {
          const button = buttons.find((candidate) => (candidate.getAttribute("aria-label") ?? candidate.textContent ?? "").trim().startsWith(name));
          return { name, box: button?.getBoundingClientRect() ?? null };
        });
        const pairs: string[] = boxes.filter((entry) => !entry.box).map((entry) => `${entry.name} not found`);
        for (let i = 0; i < boxes.length; i++) {
          for (let j = i + 1; j < boxes.length; j++) {
            const x = boxes[i].box;
            const y = boxes[j].box;
            if (!x || !y) continue;
            if (x.x < y.x + y.width && y.x < x.x + x.width && x.y < y.y + y.height && y.y < x.y + x.height) pairs.push(`${boxes[i].name} and ${boxes[j].name}`);
          }
        }
        return pairs;
      }, [...spec.buttons]);
      let previous: string[] | null = null;
      await expect.poll(async () => {
        const now = await overlapping();
        const settled = previous !== null && JSON.stringify(previous) === JSON.stringify(now);
        previous = now;
        return settled ? now : ["still laying out"];
      }, { message: "buttons that overlap" }).toEqual([]);
      await expect(page.getByRole("button", { name: "More actions" })).toHaveCount(0);
      await page.screenshot({ path: `../../artifacts/e2e-rig/rc23-1058c-${spec.kind}-plain-${size.width}.png` });
    });
  }
}

for (const spec of pages) {
  test(`the ${spec.kind} header does not run off a phone screen`, async ({ page }) => {
    const urls = await seedTitles(page);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(spec.kind === "movie" ? urls.movie : urls.show);
    const panel = page.locator("aside").filter({ hasText: "Ratings & IDs" });
    await expect(panel).toBeVisible();
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
    const panelBox = await panel.boundingBox();
    expect(panelBox!.x).toBeGreaterThanOrEqual(0);
    expect(panelBox!.x + panelBox!.width).toBeLessThanOrEqual(390);
  });
}

// A real title has ratings, an IMDb id, a TheTVDB id and the TheTVDB credit. The
// empty seeded titles above are the easy case; this is the crowded one.
for (const spec of pages) {
  test(`the ${spec.kind} panel with ratings, every id and the TheTVDB credit still fits its poster`, async ({ page }) => {
    const urls = await seedTitles(page);
    const rich = {
      metadataProvider: "tmdb",
      imdbId: "tt13111078",
      tvDbId: 388589,
      rating: 8.3,
      ratings: [
        { source: "tmdb", label: "TMDB", score: 8.3, maxScore: 10, voteCount: 1643, url: null },
        { source: "imdb", label: "IMDb", score: 8.1, maxScore: 10, voteCount: 90210, url: null }
      ],
      metadataJson: JSON.stringify({ TvDbId: "388589" })
    };
    const pattern = spec.kind === "movie" ? /\/api\/movies\/[0-9a-f-]+$/ : /\/api\/series\/[0-9a-f-]+$/;
    await page.route(pattern, async (route) => {
      const response = await route.fetch();
      await route.fulfill({ response, json: { ...(await response.json()), ...rich } });
    });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(spec.kind === "movie" ? urls.movie : urls.show);
    const panel = page.locator("aside").filter({ hasText: "Ratings & IDs" });
    await expect(panel).toBeVisible();
    await expect(panel.getByText("8.3/10")).toBeVisible();
    // Measure the content, not the panel's own minimum height.
    const heights = await panel.evaluate((element) => {
      const node = element as HTMLElement;
      node.style.minHeight = "0";
      return node.getBoundingClientRect().height;
    });
    const posterBox = await page.getByAltText(/ poster$/).or(page.getByText("No artwork yet")).first().boundingBox();
    console.log(`${spec.kind} rich content=${heights} poster=${posterBox!.height}`);
    expect(heights).toBeLessThanOrEqual(posterBox!.height);
  });
}

// #1058 (reopened): the WHOLE header card, not just the panel, fits the poster.
// A real title has a long synopsis, a long path and every fact; the middle
// column must fit beside the poster with no dead space under poster or panel.
const longOverview = Array.from({ length: 8 }, (_, i) =>
  `Sentence ${i + 1} of a long synopsis that goes on for a good while so the text cannot fit in three lines of the header.`).join(" ");

// Cause of one failure in 3,140 runs (#1191): the test ended while the page's last read of the title was still being answered by the
// route below, and Playwright reported the closed page as the test's error. Let routes in flight finish quietly.
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: "ignoreErrors" });
});

async function crowd(page: Page, kind: "movie" | "show") {
  const rich = {
    overview: longOverview,
    genres: "Horror, Thriller, Mystery, Silent film, Drama",
    metadataProvider: "tmdb",
    metadataJson: JSON.stringify({ Studio: "Decla-Bioscop AG", Director: "Robert Wiene", OriginalLanguage: "German", Network: "Some Network", Status: "Ended", Certification: "NR" }),
    ...(kind === "movie"
      ? {
          filePath: "D:\\Media\\Movies\\The Cabinet of Dr. Caligari (1920)\\The Cabinet of Dr. Caligari (1920) - Remux-1080p Bluray FLAC 2.0 x264-GROUP.mkv",
          fileSizeBytes: 21474836480,
          videoCodec: "x264",
          audioCodec: "FLAC",
          runtimeMinutes: 76
        }
      : { runtimeMinutes: 45 })
  };
  const pattern = kind === "movie" ? /\/api\/movies\/[0-9a-f-]+$/ : /\/api\/series\/[0-9a-f-]+$/;
  await page.route(pattern, async (route) => {
    const response = await route.fetch();
    await route.fulfill({ response, json: { ...(await response.json()), ...rich } });
  });
}


for (const size of [{ width: 1280, height: 900 }, { width: 1669, height: 1000 }, { width: 1920, height: 1080 }, { width: 2560, height: 1300 }]) {
  for (const spec of pages) {
    test(`the whole ${spec.kind} header card is as tall as its poster at ${size.width}px`, async ({ page }) => {
      const urls = await seedTitles(page);
      await crowd(page, spec.kind);
      await page.setViewportSize(size);
      await page.goto(spec.kind === "movie" ? urls.movie : urls.show);
      const panel = page.locator("aside").filter({ hasText: "Ratings & IDs" });
      await expect(panel).toBeVisible();
      const synopsis = page.getByTestId("title-synopsis");
      await expect(synopsis).toBeVisible();
      const more = page.getByRole("button", { name: "Read the full synopsis" });
      await expect(more).toBeVisible();

      const measureHeader = () => page.evaluate(() => {
        const box = (e: Element) => e.getBoundingClientRect();
        const grid = document.querySelector("div.grid.items-start") as HTMLElement;
        const card = grid.closest("[class*='overflow-hidden']") as HTMLElement;
        const content = grid.parentElement as HTMLElement;
        const style = getComputedStyle(content);
        const poster = (grid.querySelector("img[alt$=' poster']") ?? grid.firstElementChild) as HTMLElement;
        const middle = grid.children[1] as HTMLElement;
        const aside = grid.querySelector("aside") as HTMLElement;
        return {
          card: box(card).height,
          padding: parseFloat(style.paddingTop) + parseFloat(style.paddingBottom),
          poster: box(poster).height,
          posterBottom: box(poster).bottom,
          middleBottom: box(middle).bottom,
          asideBottom: box(aside).bottom,
          middleScroll: middle.scrollHeight - middle.clientHeight
        };
      });
      // Cause of the flake (#1191): the card was measured the moment the synopsis showed, before the poster, the fonts and the
      // metadata answer had settled, and the header lays itself out again as each arrives. Measure when two looks agree.
      await waitForLayoutCalm(page);
      let previous = "";
      await expect.poll(async () => {
        const now = JSON.stringify(await measureHeader());
        const same = now === previous;
        previous = now;
        return same;
      }, { timeout: 15_000, intervals: [250, 250, 500, 1_000] }).toBe(true);
      const m = JSON.parse(previous) as Awaited<ReturnType<typeof measureHeader>>;
      console.log(`${spec.kind} ${size.width}px card=${m.card} poster=${m.poster} padding=${m.padding}`);
      expect(m.card).toBeLessThanOrEqual(m.poster + m.padding + 2);
      expect(Math.abs(m.posterBottom - m.middleBottom)).toBeLessThanOrEqual(2);
      expect(Math.abs(m.asideBottom - m.middleBottom)).toBeLessThanOrEqual(2);
      expect(m.middleScroll).toBeLessThanOrEqual(1);
      // The More button is inside the column, and at least two lines of the synopsis show.
      const moreBox = (await more.boundingBox())!;
      expect(moreBox.y + moreBox.height).toBeLessThanOrEqual(m.middleBottom + 1);
      expect((await page.locator("[data-testid=title-synopsis] p").boundingBox())!.height).toBeGreaterThanOrEqual(40);
      await page.screenshot({ path: `../../artifacts/e2e-rig/rc23-1058c-${spec.kind}-${size.width}.png` });

      // More floats the full text over the page: the card does not change height.
      await more.click();
      const popover = page.getByTestId("title-synopsis-popover");
      await expect(popover).toBeVisible();
      await expect(popover).toContainText("Sentence 8 of a long synopsis");
      const after = await page.evaluate(() => {
        const grid = document.querySelector("div.grid.items-start") as HTMLElement;
        return (grid.closest("[class*='overflow-hidden']") as HTMLElement).getBoundingClientRect().height;
      });
      expect(Math.abs(after - m.card)).toBeLessThanOrEqual(1);
      if (size.width === 1920 || size.width === 2560) {
        await page.screenshot({ path: `../../artifacts/e2e-rig/rc23-1058c-${spec.kind}-${size.width}-popover.png` });
      }
      await page.keyboard.press("Escape");
      await expect(popover).toHaveCount(0);
      await expect(more).toBeFocused();
    });
  }
}

for (const spec of pages) {
  test(`the crowded ${spec.kind} header does not run off a phone screen`, async ({ page }) => {
    const urls = await seedTitles(page);
    await crowd(page, spec.kind);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(spec.kind === "movie" ? urls.movie : urls.show);
    await expect(page.locator("aside").filter({ hasText: "Ratings & IDs" })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
    // Stacked: the whole synopsis shows, no More button.
    await expect(page.getByRole("button", { name: "Read the full synopsis" })).toHaveCount(0);
  });
}

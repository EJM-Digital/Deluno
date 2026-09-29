import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1058. The Ratings & IDs panel stacked its buttons one per row, so the header
// was taller than the poster and left dead space. The actions now sit two to a
// row in a wider panel; the header is about as tall as the poster on wide
// screens and the panel drops under the details on a phone.

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

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
  return { movie: `/movies/${movie.id}`, show: `/tv/${series.id}` };
}

const pages = [
  { kind: "movie", buttons: ["Refresh metadata", "Edit metadata", "Edit movie", "Remove from Deluno"] },
  { kind: "show", buttons: ["Refresh metadata", "Edit metadata", "Remove from Deluno"] }
] as const;

for (const size of [{ width: 1669, height: 1000 }, { width: 1920, height: 1080 }]) {
  for (const spec of pages) {
    test(`the ${spec.kind} header is no taller than its poster and its buttons do not overlap at ${size.width}px`, async ({ page }) => {
      const urls = await seedTitles(page);
      await page.setViewportSize(size);
      await page.goto(spec.kind === "movie" ? urls.movie : urls.show);
      const panel = page.locator("aside").filter({ hasText: "Ratings & IDs" });
      await expect(panel).toBeVisible();

      const poster = page.getByAltText(/ poster$/).or(page.getByText("No artwork yet")).first();
      const posterBox = await poster.boundingBox();
      // The grid holding poster, details and panel: the box that grew.
      const headerBox = await panel.locator("xpath=..").boundingBox();
      const panelBox = await panel.boundingBox();
      expect(posterBox).not.toBeNull();
      expect(headerBox).not.toBeNull();
      console.log(`${spec.kind} ${size.width}px poster=${posterBox!.height} header=${headerBox!.height} panel=${panelBox!.height}`);
      expect(headerBox!.height).toBeLessThanOrEqual(posterBox!.height + 24);

      const boxes = [];
      const names = [...spec.buttons, "Rescan the folder", "Preview the folder rename"];
      for (const name of names) {
        const button = panel.getByRole("button", { name: new RegExp(`^${name}`) });
        await expect(button).toBeVisible();
        boxes.push({ name, box: (await button.boundingBox())! });
      }
      for (let i = 0; i < boxes.length; i++) {
        for (let j = i + 1; j < boxes.length; j++) {
          const a = boxes[i].box;
          const b = boxes[j].box;
          const overlaps = a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
          expect(overlaps, `${boxes[i].name} and ${boxes[j].name} overlap`).toBe(false);
        }
      }
      // Remove from Deluno stays last.
      const remove = boxes.find((entry) => entry.name === "Remove from Deluno")!.box;
      for (const entry of boxes) expect(entry.box.y + entry.box.height).toBeLessThanOrEqual(remove.y + remove.height + 1);
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

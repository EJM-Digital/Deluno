import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * #1119: a list's columns can be dragged into a new order and sorted by
 * clicking a header; both are remembered in this browser, and "Reset columns"
 * (a menu item behind the card's Columns button) puts them back.
 */
test.describe("list columns", () => {
  test.skip(({ isMobile }) => isMobile, "Columns collapse on a phone; the drag is a pointer gesture on the wide layout.");

  let authToken: string | null = null;
  const created: string[] = [];

  test.beforeEach(async ({ page }) => {
    await authenticateAndNavigate(page, "/indexers/indexers");
    authToken = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  });

  test.afterEach(async ({ page }) => {
    for (const id of created.splice(0)) {
      await page.request.delete(`/api/indexers/${id}`, { headers: { Authorization: `Bearer ${authToken}` } });
    }
  });

  async function addIndexer(page: Page, name: string, protocol: string) {
    const response = await page.request.post("/api/indexers", {
      headers: { Authorization: `Bearer ${authToken}` },
      data: { name, protocol, privacy: "private", baseUrl: `https://${name}`, apiKey: null, priority: 10, categories: "2000", tags: "", mediaScope: "movies", isEnabled: true }
    });
    expect(response.ok(), `POST /api/indexers failed: ${response.status()}`).toBe(true);
    created.push(((await response.json()) as { id: string }).id);
  }

  const headerOrder = (page: Page) => page.getByRole("columnheader").evaluateAll((headers) => headers.map((header) => (header as HTMLElement).dataset.colId));
  // The Name cells, wherever the Name column has been moved to.
  const nameCells = async (page: Page) => {
    const index = (await headerOrder(page)).indexOf("name");
    return page.locator('#main-content [role="rowgroup"] [role="row"]').evaluateAll((rows, at) => rows.map((row) => row.querySelectorAll('[role="cell"]')[at]?.textContent ?? ""), index);
  };

  async function dragHeader(page: Page, from: string, onto: string) {
    const source = await page.locator(`[role="columnheader"][data-col-id="${from}"]`).boundingBox();
    const target = await page.locator(`[role="columnheader"][data-col-id="${onto}"]`).boundingBox();
    if (!source || !target) throw new Error("column headers not found");
    const y = source.y + source.height / 2;
    await page.mouse.move(source.x + 12, y);
    await page.mouse.down();
    await page.mouse.move(source.x + 24, y, { steps: 3 });
    // Mid-drag the header is lifted: it follows the pointer by transform and nothing else on the page moves.
    await expect(page.locator(`[role="columnheader"][data-col-id="${from}"]`)).toHaveCSS("z-index", "3");
    await page.mouse.move(target.x + 10, y, { steps: 12 });
    await page.mouse.up();
  }

  test("drag a column, sort another, reload and see both kept, then Reset columns", async ({ page }) => {
    const stamp = Date.now();
    await addIndexer(page, `zz-b-${stamp}.example.test`, "torznab");
    await addIndexer(page, `zz-a-${stamp}.example.test`, "newznab");
    await page.goto("/indexers/indexers");
    await expect(page.locator("#main-content").getByText(`zz-a-${stamp}.example.test`).first()).toBeVisible();

    const defaultOrder = await headerOrder(page);
    expect(defaultOrder.slice(0, 3)).toEqual(["name", "protocol", "usedFor"]);
    await expect(page.getByRole("button", { name: "Columns" })).toHaveCount(1);

    // Move Protocol in front of Name.
    await dragHeader(page, "protocol", "name");
    await expect.poll(() => headerOrder(page).then((order) => order.slice(0, 2))).toEqual(["protocol", "name"]);

    // Sort by Name: ascending, then descending.
    const nameSort = page.locator('[role="columnheader"][data-col-id="name"]').getByRole("button");
    await nameSort.click();
    await expect(page.locator('[role="columnheader"][data-col-id="name"]')).toHaveAttribute("aria-sort", "ascending");
    const ascending = (await nameCells(page)).filter((text) => text.includes(`${stamp}`));
    expect(ascending.length).toBe(2);
    await nameSort.click();
    await expect(page.locator('[role="columnheader"][data-col-id="name"]')).toHaveAttribute("aria-sort", "descending");
    const descending = (await nameCells(page)).filter((text) => text.includes(`${stamp}`));
    expect(descending).toEqual([...ascending].reverse());

    // A third click reverses again; there is no click that goes back to the default order.
    await nameSort.click();
    await expect(page.locator('[role="columnheader"][data-col-id="name"]')).toHaveAttribute("aria-sort", "ascending");
    await nameSort.click();
    await expect(page.locator('[role="columnheader"][data-col-id="name"]')).toHaveAttribute("aria-sort", "descending");

    // Remembered across a reload.
    await page.reload();
    await expect(page.locator('[role="columnheader"][data-col-id="name"]')).toHaveAttribute("aria-sort", "descending");
    expect((await headerOrder(page)).slice(0, 2)).toEqual(["protocol", "name"]);

    // Reset columns puts the order and the sort back.
    await page.getByRole("button", { name: "Columns", exact: true }).click();
    await page.getByRole("menuitem", { name: "Reset columns" }).click();
    await expect(page.locator('[role="columnheader"][data-col-id="name"]')).not.toHaveAttribute("aria-sort");
    expect((await headerOrder(page)).slice(0, 3)).toEqual(["name", "protocol", "usedFor"]);
  });
});

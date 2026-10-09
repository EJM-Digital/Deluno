import os from "node:os";
import { expect as baseExpect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// Saving an indexer closes its drawer only once the server has answered, which on a busy machine takes longer than 5 s (#1191).
const expect = baseExpect.configure({ timeout: 20_000 });

// 8 Oct rig walk: a Torznab indexer added at a local address was named after the computer Deluno runs on, because the
// add drawer had no name field. The drawer now has one. Left blank it shows the address's own host name as its hint and
// that is the name the indexer gets; typed, the typed name is kept. Never the machine's name.

test.skip(({ isMobile }) => isMobile, "The Add indexer drawer is the same on a phone.");

async function listIndexers(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  const response = await page.request.get("/api/indexers", { headers: { Authorization: `Bearer ${token}` } });
  return await response.json() as { id: string; name: string; baseUrl: string }[];
}

async function addTorznab(page: Page, url: string, name?: string) {
  await page.goto("/indexers/indexers");
  await page.getByRole("button", { name: "New indexer" }).first().click();
  const drawer = page.getByRole("dialog");
  await drawer.getByLabel("Protocol").selectOption("torznab");
  await drawer.getByLabel("Torznab URL").fill(url);
  await drawer.getByLabel("API key").fill("draft-key");
  const field = drawer.getByLabel("Name", { exact: true });
  if (name) await field.fill(name);
  return { drawer, field };
}

test("a Torznab indexer is named after its address by default and keeps a name typed for it", async ({ page }) => {
  await authenticateAndNavigate(page, "/indexers/indexers");
  const made: string[] = [];
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  try {
    // Blank name, an address on this computer: the hint is the address's host, and so is the name.
    const first = await addTorznab(page, "http://localhost:9117/api/v2.0/indexers/fit-check/results/torznab/");
    await expect(first.field).toHaveAttribute("placeholder", "localhost");
    await first.drawer.getByRole("button", { name: "Add indexer" }).click();
    await expect(page.getByRole("button", { name: "Add indexer" })).toHaveCount(0);
    await page.keyboard.press("Escape");

    // A typed name is kept.
    const second = await addTorznab(page, "https://tracker.example.test/api", "My private tracker");
    await second.drawer.getByRole("button", { name: "Add indexer" }).click();
    await expect(page.getByRole("button", { name: "Add indexer" })).toHaveCount(0);
    await page.keyboard.press("Escape");

    const indexers = await listIndexers(page);
    const local = indexers.find((item) => item.baseUrl.includes("localhost:9117"));
    const typed = indexers.find((item) => item.baseUrl.includes("tracker.example.test"));
    if (local) made.push(local.id);
    if (typed) made.push(typed.id);
    expect(typed?.name).toBe("My private tracker");
    expect(local?.name).toBeTruthy();
    expect(local!.name.toLowerCase()).not.toBe(os.hostname().toLowerCase());
    // Reopened, the saved name is what the field holds.
    await page.goto("/indexers/indexers");
    await page.getByRole("row").filter({ hasText: "My private tracker" }).first().click();
    await expect(page.getByRole("dialog").getByLabel("Name", { exact: true })).toHaveValue("My private tracker");
  } finally {
    // Cause of the flake (#1191): the indexers were only noted for removal once both had been saved and listed, so a slow save that
    // timed out left them behind, and every later spec in the shared database then saw an indexer it did not expect (a "0 of 1 ready"
    // heading, an unhealthy mark in the navigation). Remove whatever this test made, found by its addresses.
    for (const item of await listIndexers(page).catch(() => [])) {
      if ((item.baseUrl.includes("localhost:9117") || item.baseUrl.includes("tracker.example.test")) && !made.includes(item.id)) made.push(item.id);
    }
    for (const id of made) await page.request.delete(`/api/indexers/${id}`, { headers: { Authorization: `Bearer ${token}` } });
  }
});

import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// rc.30 live test, #1141. Walking the Dashboard's guided setup, a person saw System
// lit up in the sidebar, the address they connect from as "::ffff:...", and an
// "Other devices" step that said it was checking and never showed what it found.

async function sidebarColour(page: Page, name: RegExp): Promise<string> {
  const link = page.getByRole("navigation").getByRole("link", { name }).first();
  return link.evaluate((element) => getComputedStyle(element).color);
}

test("the sidebar does not light System up for the guided setup (#1141)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The sidebar is a drawer on a phone.");
  await authenticateAndNavigate(page, "/system");
  const onSystem = await sidebarColour(page, /^System/);
  const other = await sidebarColour(page, /^In progress/);
  // Proves the check can tell a lit link from a quiet one.
  expect(onSystem).not.toBe(other);

  await page.goto("/setup-guide");
  await expect(page.getByRole("heading", { name: "Guided setup" }).or(page.getByText("Get Deluno working first.")).first()).toBeVisible();
  expect(await sidebarColour(page, /^System/)).toBe(await sidebarColour(page, /^In progress/));
});

test("the sign-in question names the address plainly and the Other devices step shows its result (#1141)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The step list is the same on a phone; the desktop run proves the words.");
  // The server reports the address it saw in its IPv4-mapped form, as it did on the rig; only that one value is changed.
  await page.route("**/api/security/local-network-auth", async (route) => {
    const response = await route.fetch();
    const overview = await response.json() as { currentAddress?: string };
    await route.fulfill({ response, json: { ...overview, currentAddress: "::ffff:192.168.1.20" } });
  });
  await authenticateAndNavigate(page, "/setup-guide");
  await page.getByRole("button", { name: /How you sign in/ }).click();
  const main = page.locator("#main-content");
  await expect(main.getByText(/You are connecting from/)).toBeVisible();
  const said = (await main.getByText(/You are connecting from/).innerText()).replace(/\s+/g, " ");
  expect(said).not.toContain("::ffff");
  expect(said).toContain("192.168.1.20");

  await page.getByRole("button", { name: /Other devices/ }).click();
  const result = main.getByRole("status", { name: "Other devices on your network" });
  await expect(result).toBeVisible();
  // It shows what the check found: not left at "Checking...".
  await expect(result).toHaveText(/Other devices can reach Deluno|Nothing to allow here|Deluno could not check|cannot reach Deluno/i);
});

import { expect, test } from "@playwright/test";
import { authenticateAndNavigate, fallbackCredentials } from "../helpers/auth-helper";

// rc.30 live test, #1130 and #1149: what the person saw on Find & Download >
// Download Clients straight after an install, and in the sidebar's account button.
// The installer is not in the smoke host, so the list of installed apps is the one
// stubbed thing; every click and every read is on the real page.

const installedApps = [
  { kind: "nzbget", displayName: "NZBGet", category: "download-client", installed: true, reachable: true, defaultHost: "127.0.0.1", defaultPort: 6789 },
  { kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, reachable: true, defaultHost: "127.0.0.1", defaultPort: 8081 },
  { kind: "transmission", displayName: "Transmission", category: "download-client", installed: true, reachable: true, defaultHost: "127.0.0.1", defaultPort: 9091 }
];

test("the Connect the apps panel lines up and every app shows Connected after Connect all, without a reload (#1130)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The column alignment is about the desktop list; the phone shows the same rows stacked.");
  await authenticateAndNavigate(page, "/");
  let connected = false;
  const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  await page.route(/\/api\/system\/bundled-components$/, (route) =>
    route.fulfill(json(installedApps.map((app) => ({ ...app, connected })))));
  await page.route(/\/api\/system\/bundled-components\/connect-all$/, (route) => {
    connected = true;
    return route.fulfill(json({ results: installedApps.map((app) => ({ kind: app.kind, name: app.displayName, healthy: true, message: `${app.displayName} is connected.` })) }));
  });

  let navigations = 0;
  page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) navigations += 1; });
  await page.goto("/indexers/download-clients");
  const panel = page.getByRole("region", { name: "Connect the apps you installed" }).first();
  await expect(panel).toBeVisible();
  await expect(panel.getByText("Not connected")).toHaveCount(3);
  const loaded = navigations;

  // The status chips share one left edge, and so do the Connect buttons: a column, not a ragged edge.
  const lefts = async (locator: ReturnType<typeof panel.getByText>) =>
    Promise.all((await locator.all()).map(async (item) => Math.round((await item.boundingBox())!.x)));
  const chipLefts = await lefts(panel.getByText("Not connected"));
  expect(new Set(chipLefts).size, `status chips start at ${chipLefts.join(", ")}`).toBe(1);
  const buttons = panel.getByRole("button", { name: /^Connect (NZBGet|qBittorrent|Transmission)$/ });
  const rights = await Promise.all((await buttons.all()).map(async (item) => { const box = (await item.boundingBox())!; return Math.round(box.x + box.width); }));
  expect(rights).toHaveLength(3);
  expect(new Set(rights).size, `Connect buttons end at ${rights.join(", ")}`).toBe(1);

  await panel.getByRole("button", { name: "Connect all" }).click();

  // The panel follows the connection itself, with no reload: nothing is left to press, and it says so in one line.
  await expect(page.getByText("All the apps you installed are connected.")).toBeVisible();
  await expect(page.getByRole("button", { name: /^Connect (all|NZBGet|qBittorrent|Transmission)$/ })).toHaveCount(0);
  await expect(page.getByText("Not connected")).toHaveCount(0);
  expect(navigations).toBe(loaded);
});

// The issue also asks that the buttons share a left edge: each fills the list's action column, so they line up whatever
// their labels ("Connect NZBGet", "Connect qBittorrent"), on a desktop and on a phone.
test("every Connect button in the panel starts at the same left edge (#1130)", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  await page.route(//api/system/bundled-components$/, (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(installedApps.map((app) => ({ ...app, connected: false }))) }));
  await page.goto("/indexers/download-clients");
  const panel = page.getByRole("region", { name: "Connect the apps you installed" }).first();
  await expect(panel.getByText("Not connected")).toHaveCount(3);
  const lefts = await Promise.all((await panel.getByRole("button", { name: /^Connect (NZBGet|qBittorrent|Transmission)$/ }).all())
    .map(async (item) => Math.round((await item.boundingBox())!.x)));
  expect(new Set(lefts).size, `Connect buttons start at ${lefts.join(", ")}`).toBe(1);
});

test("the sidebar account button shows the person's name and nothing that repeats it (#1149)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The phone keeps the account menu behind the navigation drawer.");
  await authenticateAndNavigate(page, "/");
  const button = page.getByTestId("user-menu");
  await expect(button).toBeVisible();
  const text = ((await button.innerText()) ?? "").replace(/\s+/g, " ").trim();
  expect(text).toContain(fallbackCredentials.displayName);
  // No "@handle" and no "Deluno on <computer>" second line.
  expect(text).not.toMatch(/@/);
  expect(text).not.toMatch(/Deluno on/i);
  expect(text.replace(fallbackCredentials.displayName, "").replace(/^[A-Z]{1,3}\s*/, "").trim()).toBe("");
});

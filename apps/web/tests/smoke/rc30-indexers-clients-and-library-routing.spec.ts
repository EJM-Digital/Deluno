import { mkdirSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// rc.30 live test, #1134, #1135 and #1136, on Find & Download. A person adds an
// indexer in its drawer, tests it there, and presses Add. The indexer should keep
// what the test said, and should be searched by the library it is "used for"
// without a second round of switches in Library Routing. Download clients that are
// connected should reach the libraries the same way, and the Library Routing drawer
// should say what a category is once, not once per client. The indexer goes in
// through its drawer; the library and the clients are only the stage.

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

test("an indexer added from its drawer keeps its test result and is searched by the library it is used for (#1134, #1135)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The Add indexer drawer is the same on a phone; the desktop run proves the behaviour.");
  test.setTimeout(90_000);
  await authenticateAndNavigate(page, "/movies");
  const headers = await authHeaders(page);
  const stamp = Date.now();
  const root = path.join(os.tmpdir(), "deluno-playwright-libraries", `routing-${stamp}`);
  mkdirSync(root, { recursive: true });
  const libraryName = `Routing-Movies-${stamp}`;
  const libraryResponse = await page.request.post("/api/libraries", {
    headers,
    data: { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: root, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false }
  });
  expect(libraryResponse.ok(), `POST /api/libraries failed: ${libraryResponse.status()}`).toBe(true);
  const library = await libraryResponse.json() as { id: string };
  const host = `rc30-${stamp}.example.test`;

  try {
    // The drawer's own test says Healthy. Only the answer of the test is stubbed: the host does not exist.
    await page.route("**/api/indexers/test", (route) => route.fulfill({
      json: { healthStatus: "healthy", message: `Reached ${host} and received a valid Newznab response.`, latencyMs: 12 }
    }));

    await page.goto("/indexers/indexers");
    await page.getByRole("button", { name: "New indexer" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New indexer" });
    await drawer.getByLabel("Protocol").selectOption("newznab");
    await drawer.getByLabel("Newznab URL").fill(`https://${host}`);
    await drawer.getByLabel("API key").fill("draft-key");
    await drawer.getByRole("radio", { name: "Movies", exact: true }).click();
    await drawer.getByRole("button", { name: "Test", exact: true }).click();
    await expect(drawer.getByText(`Reached ${host} and received a valid Newznab response.`)).toBeVisible();
    await drawer.getByRole("button", { name: "Add indexer" }).click();

    // 1134: Add keeps what the drawer's test said. The saved indexer opens with its result, not "Not tested yet".
    const saved = page.getByRole("dialog", { name: host });
    await expect(saved).toBeVisible();
    await expect(saved.getByText(`Reached ${host} and received a valid Newznab response.`)).toBeVisible();
    await expect(saved.getByText("Not tested yet")).toHaveCount(0);
    await page.keyboard.press("Escape");
    await expect(saved).toHaveCount(0);
    const row = page.getByRole("row").filter({ hasText: host });
    await expect(row).toBeVisible();
    await expect(row.getByText("Healthy", { exact: true })).toBeVisible();
    await expect(row.getByText("Untested")).toHaveCount(0);

    // 1135: Library Routing already shows the movie library searching it, with nothing switched on by hand.
    await page.goto("/indexers/library-routing");
    const routed = page.getByRole("row").filter({ hasText: libraryName });
    await expect(routed).toBeVisible();
    await expect(routed).toContainText(host);
    await expect(routed).not.toContainText("Deluno can't search for this library");
  } finally {
    const list = await page.request.get("/api/indexers", { headers, timeout: 10_000 }).catch(() => null);
    const items = list ? await list.json() as Array<{ id: string; name: string; baseUrl?: string }> : [];
    for (const item of items.filter((candidate) => `${candidate.name} ${candidate.baseUrl ?? ""}`.includes(host))) {
      await page.request.delete(`/api/indexers/${item.id}`, { headers, timeout: 10_000 }).catch(() => undefined);
    }
    await page.request.delete(`/api/libraries/${library.id}`, { headers, timeout: 10_000 }).catch(() => undefined);
  }
});

test("a connected download client reaches the library by itself, and the category paragraph is said once (#1135, #1136)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The routing drawer is the same on a phone; the desktop run proves the behaviour.");
  test.setTimeout(90_000);
  await authenticateAndNavigate(page, "/movies");
  const headers = await authHeaders(page);
  const stamp = Date.now();
  const root = path.join(os.tmpdir(), "deluno-playwright-libraries", `clients-${stamp}`);
  mkdirSync(root, { recursive: true });
  const libraryName = `Routing-Clients-${stamp}`;
  const libraryResponse = await page.request.post("/api/libraries", {
    headers,
    data: { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: root, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false }
  });
  expect(libraryResponse.ok(), `POST /api/libraries failed: ${libraryResponse.status()}`).toBe(true);
  const library = await libraryResponse.json() as { id: string };
  const clientIds: string[] = [];

  try {
    for (const [index, protocol] of ["qbittorrent", "deluge", "transmission"].entries()) {
      const created = await page.request.post("/api/download-clients", {
        headers,
        data: {
          name: `${protocol} on rc30-${stamp}-${index}.example.test`, protocol, host: `rc30-${stamp}-${index}.example.test`, port: 8080 + index,
          username: null, password: null, endpointUrl: null, moviesCategory: "movies", tvCategory: "tv", categoryTemplate: null, priority: 10, isEnabled: true
        }
      });
      expect(created.ok(), `POST /api/download-clients failed: ${created.status()}`).toBe(true);
      clientIds.push((await created.json() as { id: string }).id);
    }

    await page.goto("/indexers/library-routing");
    const routed = page.getByRole("row").filter({ hasText: libraryName });
    await expect(routed).toBeVisible();
    // A client added to Deluno is already used by the movie library: not "Nowhere to send releases".
    await expect(routed).not.toContainText("Nowhere to send releases");

    await routed.click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Download clients", { exact: true }).first()).toBeVisible();
    // The category is explained once, above the client list, however many clients there are.
    await expect(drawer.getByText(/A category is the label Deluno sends with a download/)).toHaveCount(1);
    // The first client for the library is already on; the others are listed, ready to be switched on.
    await expect(drawer.getByRole("switch", { name: new RegExp(`qbittorrent on rc30-${stamp}`, "i") })).toBeChecked();
    for (const protocol of ["deluge", "transmission"]) {
      await expect(drawer.getByRole("switch", { name: new RegExp(`${protocol} on rc30-${stamp}`, "i") })).toBeVisible();
    }
  } finally {
    for (const id of clientIds) await page.request.delete(`/api/download-clients/${id}`, { headers, timeout: 10_000 }).catch(() => undefined);
    await page.request.delete(`/api/libraries/${library.id}`, { headers, timeout: 10_000 }).catch(() => undefined);
  }
});

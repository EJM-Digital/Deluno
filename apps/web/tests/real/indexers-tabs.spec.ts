import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download, the tab strip, the old addresses, the toolbar button of each tab and the library-routing deep link, against the
 * real server (measure 4, issue 1296). Nothing here reaches another app.
 */

const stamp = `tb${Date.now()}`;

test.describe("Find & Download tabs", () => {
  test.setTimeout(90_000);

  test("the old Settings addresses for indexers, download clients and connect land on Find & Download", async ({ page }) => {
    await openScreen(page, "/");
    for (const [from, to] of [["/settings/indexers", "/indexers/indexers"], ["/settings/download-clients", "/indexers/download-clients"], ["/settings/connect", "/indexers/indexers"]]) {
      await page.goto(from);
      await untilQuiet(page);
      await expect(page).toHaveURL(new RegExp(`${to}$`));
    }
  });

  test("the tab strip offers Proxies, Scoreboard, Remote Path Mappings, Subtitle Providers and Media Servers and each opens its own screen", async ({ page }) => {
    await openScreen(page, "/indexers/indexers");
    await untilQuiet(page);
    for (const [name, address] of [["Proxies", "/indexers/proxies"], ["Scoreboard", "/indexers/scoreboard"], ["Remote Path Mappings", "/indexers/path-mappings"], ["Subtitle Providers", "/indexers/subtitle-providers"], ["Media Servers", "/indexers/media-servers"]]) {
      await page.goto(address);
      await untilQuiet(page);
      await expect(page.getByRole("link", { name, exact: true }).first()).toHaveAttribute("aria-current", "page");
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
    }
  });

  test("each tab has its own toolbar button: New indexer, New client, New proxy, Add server, and none on Scoreboard or Remote path mappings", async ({ page }) => {
    await openScreen(page, "/indexers/indexers");
    for (const [address, button] of [["/indexers/indexers", "New indexer"], ["/indexers/download-clients", "New client"], ["/indexers/proxies", "New proxy"], ["/indexers/media-servers", "Add server"]] as const) {
      await page.goto(address);
      await untilQuiet(page);
      await expect(page.getByRole("button", { name: button }).first()).toBeVisible();
    }

    for (const address of ["/indexers/scoreboard", "/indexers/path-mappings"]) {
      await page.goto(address);
      await untilQuiet(page);
      for (const button of ["New indexer", "New client", "New proxy", "Add server"]) await expect(page.getByRole("button", { name: button })).toHaveCount(0);
    }

    // Each tab shows only its own button.
    await page.goto("/indexers/proxies");
    await untilQuiet(page);
    await expect(page.getByRole("button", { name: "New indexer" })).toHaveCount(0);
  });

  test("the library-routing deep link opens that library's routing drawer and takes the query off the address", async ({ page }) => {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-tabs-"));
    await openScreen(page, "/indexers/indexers");
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Deep ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    try {
      await page.goto(`/indexers/library-routing?libraryId=${library.body.id}`);
      await untilQuiet(page);
      const drawer = page.getByRole("dialog");
      await expect(drawer).toBeVisible();
      await expect(drawer).toContainText(`Deep ${stamp}`);
      await expect(page).toHaveURL(/\/indexers\/library-routing$/);
    } finally {
      await api(page, "DELETE", `/api/libraries/${library.body.id}`);
      fs.rmSync(folder, { recursive: true, force: true });
    }
  });
});

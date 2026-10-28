import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The Settings tab strips against the real server (measure 4, issue 1329): the Quality & Release strip moves between its five
 * screens and marks the one it is on, and Library & Storage shows its completion tick once a library exists. The library is a
 * real record on a temp folder; nothing reaches another app.
 */

const stamp = `st${Date.now()}`;

test.describe("Settings tab strips", () => {
  test.setTimeout(120_000);
  let folder = "";
  let libraryId = "";

  test.beforeEach(async ({ page }) => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-tabs-"));
    await openScreen(page, "/settings/policy-sets");
    await untilQuiet(page);
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Tabs ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryId = made.body.id;
  });

  test.afterEach(async ({ page }) => {
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("each tab of the Quality & Release strip opens its own screen and is the only one marked as the current page", async ({ page }) => {
    await page.setViewportSize({ width: 2400, height: 1000 }); // wide enough that no tab folds into "More"
    await page.reload();
    await untilQuiet(page);
    const strip = page.getByRole("navigation", { name: "Sections" });
    const tabs: Array<[string, RegExp]> = [
      ["Your Libraries", /\/settings\/policy-sets$/],
      ["Quality Profiles", /\/settings\/profiles$/],
      ["Size Rules", /\/settings\/quality$/],
      ["Release Preferences", /\/settings\/custom-formats$/],
      ["Acquisition Rules", /\/settings\/release-rules$/]
    ];
    for (const [label, url] of tabs) {
      await strip.getByRole("link", { name: label }).click();
      await expect(page).toHaveURL(url);
      await untilQuiet(page);
      await expect(strip.getByRole("link", { name: label })).toHaveAttribute("aria-current", "page");
      await expect(strip.locator("a[aria-current='page']")).toHaveCount(1);
    }
  });

  test("Library & Storage carries the complete mark once a library exists, and the library list shows it", async ({ page }) => {
    await page.goto("/settings/libraries");
    await untilQuiet(page);
    const tab = page.getByRole("navigation", { name: "Sections" }).getByRole("link", { name: /^Library & Storage/ });
    await expect(tab).toHaveAttribute("aria-label", "Library & Storage — complete");
    await expect(tab).toHaveAttribute("data-status", "complete");
    await expect(page.getByRole("row").filter({ hasText: `Tabs ${stamp}` })).toHaveCount(1);
  });
});

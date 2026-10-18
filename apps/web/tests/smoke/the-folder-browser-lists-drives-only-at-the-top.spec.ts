import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// 8 Oct rig walk: inside every folder, the folder browser showed a drive ("Media (Z:)") at the top of the list. Only
// the list of drives shows drives: inside a folder, the rows are that folder's own folders.

test.skip(({ isMobile }) => isMobile, "The folder browser is the same on a phone.");

test("inside a folder the browser lists only that folder's folders, never a drive", async ({ page }) => {
  test.setTimeout(90_000);
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-folder-browser-"));
  fs.mkdirSync(path.join(root, "Movies"));
  fs.mkdirSync(path.join(root, "TV", "Shows"), { recursive: true });
  try {
    await authenticateAndNavigate(page, "/settings/libraries");
    // The Windows window opens on the server's own screen; say it cannot, to get the in-app browser.
    await page.route("**/api/filesystem/native-folder-picker", (route) => route.fulfill({ status: 409, contentType: "application/json", body: JSON.stringify({ message: "Unavailable." }) }));
    await page.getByRole("button", { name: "New library", exact: true }).first().click();
    const drawer = page.getByRole("dialog", { name: "New library" });
    await drawer.getByRole("textbox", { name: "Library folder" }).fill(root);
    await drawer.getByRole("button", { name: "Choose folder", exact: true }).first().click();

    const browser = page.getByRole("dialog", { name: /Choose .*folder/ });
    await expect(browser).toBeVisible();
    const rowsOf = () => browser.locator("button.min-w-0.flex-1");
    // The folder's own two folders, and nothing else: no drive, no example path.
    await expect(rowsOf()).toHaveCount(2);
    await expect(browser.getByText("Movies", { exact: true })).toBeVisible();
    await expect(browser.getByText("TV", { exact: true })).toBeVisible();
    for (const word of ["Network drive", "Local drive", "External drive", "Optical drive", "Filesystem root"]) {
      await expect(browser.getByText(word)).toHaveCount(0);
    }

    // One level further down.
    await browser.getByText("TV", { exact: true }).click();
    await expect(browser.getByText("Shows", { exact: true })).toBeVisible();
    await expect(rowsOf()).toHaveCount(1);

    // The list of drives, one click away, is where drives are.
    await browser.getByRole("button", { name: "Roots", exact: true }).click();
    // Asking the machine for its drives can take long when a mapped network drive is asleep: wait for the list itself.
    await expect(browser.getByText(/Local drive|Filesystem root/).first()).toBeVisible({ timeout: 60_000 });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

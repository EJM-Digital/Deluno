import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The folder field, on the New library drawer, against the real server and a real folder tree, nothing stubbed
 * (measure 4, issue 1270): Check says what the server can see at the path, Choose folder browses the server's own
 * folders, and a typed path is used as it stands.
 */

const stamp = `pi${Date.now()}`;
let base = "";
let movies = "";

test.describe("Folder field", () => {
  test.beforeAll(() => {
    base = fs.mkdtempSync(path.join(os.tmpdir(), `deluno-real-pathinput-${stamp}-`));
    movies = path.join(base, "movies");
    fs.mkdirSync(path.join(movies, "inner"), { recursive: true });
  });
  test.afterAll(() => fs.rmSync(base, { recursive: true, force: true }));

  async function openDrawer(page: import("@playwright/test").Page) {
    // From this machine's own browser the server would open a Windows folder window on the computer's screen, where a test must never
    // open one. The server refuses that ask from any other device with exactly this answer, which sends the screen to its in-app folder browser.
    await page.route("**/api/filesystem/native-folder-picker", (route) => route.fulfill({
      status: 403, contentType: "application/json",
      body: JSON.stringify({ message: "The Windows folder window opens on the computer Deluno runs on. Use the folder browser instead.", nativePickerUnavailable: true })
    }));
    await openScreen(page, "/settings/libraries");
    await untilQuiet(page);
    await page.getByRole("button", { name: "New library", exact: true }).first().click();
    const drawer = page.getByRole("dialog", { name: "New library" });
    await expect(drawer).toBeVisible();
    return drawer;
  }

  test("Check reads the folder the server can see, in words and badges, and says so when there is no such folder", async ({ page }) => {
    const drawer = await openDrawer(page);
    const field = drawer.getByRole("textbox", { name: "Library folder" });
    await field.fill(movies);
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Check", exact: true }).first().click();
    await expect(drawer.getByText("Exists", { exact: true }).first()).toBeVisible();
    await expect(drawer.getByText("Directory", { exact: true }).first()).toBeVisible();
    await expect(drawer.getByText("Readable", { exact: true }).first()).toBeVisible();
    await expect(drawer.getByText("Writable", { exact: true }).first()).toBeVisible();
    requests.stop();
    expect(requests.seen.filter((r) => /filesystem/i.test(r.path)).length).toBeGreaterThan(0);

    await field.fill(path.join(base, "nothing-here"));
    await drawer.getByRole("button", { name: "Check", exact: true }).first().click();
    await expect(drawer.getByText(/does not exist|doesn.t exist|not found|cannot find|no such/i).first()).toBeVisible();
  });

  test("Choose folder browses the server's folders: a typed path is used as typed, Roots, Open, Up and Select move about and choose", async ({ page }) => {
    const drawer = await openDrawer(page);
    const field = drawer.getByRole("textbox", { name: "Library folder" });

    await drawer.getByRole("button", { name: "Choose folder", exact: true }).first().click();
    const sheet = page.getByRole("dialog", { name: /^Choose .* folder$/ });
    await expect(sheet).toBeVisible();

    // A typed path: Check in the sheet reads it, and Use typed path puts it in the field and closes the sheet.
    const typed = sheet.getByRole("textbox", { name: /Type or paste a path/ });
    await typed.fill(movies);
    await sheet.getByRole("button", { name: "Check", exact: true }).click();
    await expect(sheet.getByText("Exists", { exact: true }).first()).toBeVisible();
    await sheet.getByRole("button", { name: "Use typed path", exact: true }).click();
    await expect(sheet).toHaveCount(0);
    await expect(field).toHaveValue(movies);

    // Browsing: Roots lists the server's roots; Open goes into one and Up comes back out.
    await drawer.getByRole("button", { name: "Choose folder", exact: true }).first().click();
    await expect(sheet).toBeVisible();
    await sheet.getByRole("button", { name: "Roots", exact: true }).click();
    await expect(sheet.getByText("Computer roots")).toBeVisible();
    const openButtons = sheet.getByRole("button", { name: "Open", exact: true });
    await expect(openButtons.first()).toBeVisible();
    await openButtons.first().click();
    await expect(sheet.getByText("Computer roots")).toHaveCount(0);
    const up = sheet.getByRole("button", { name: "Up", exact: true });
    if (await up.isEnabled()) {
      await up.click();
    }

    // Typing the parent's path and opening it by the listing: the folder inside is offered with Open and Select.
    await typed.fill(movies);
    await sheet.getByRole("button", { name: "Check", exact: true }).click();
    await sheet.getByRole("button", { name: "Use typed path", exact: true }).click();
    await drawer.getByRole("button", { name: "Choose folder", exact: true }).first().click();
    await expect(sheet.getByText(movies).first()).toBeVisible();
    await expect(sheet.getByText("inner", { exact: true })).toBeVisible();
    await sheet.getByRole("button", { name: "Select", exact: true }).first().click();
    await expect(sheet).toHaveCount(0);
    await expect(field).toHaveValue(path.join(movies, "inner"));
  });
});

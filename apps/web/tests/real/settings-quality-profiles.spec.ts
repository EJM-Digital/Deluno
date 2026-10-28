import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Quality profiles against the real server (measure 4, issue 1296). The profile and the libraries are real records on a
 * temp folder; nothing reaches another app.
 */

const stamp = `qp${Date.now()}`;
const name = `HD films ${stamp}`;

interface Profile { id: string; name: string }
const profiles = async (page: Page) => (await api<Profile[]>(page, "GET", "/api/quality-profiles")).body;
const row = (page: Page, text: string) => page.getByRole("row").filter({ hasText: text }).first();

test.describe("Quality profiles", () => {
  test.setTimeout(90_000);
  let folder = "";
  let libraryId = "";
  let profileId = "";

  test.beforeEach(async ({ page }) => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-qp-"));
    await openScreen(page, "/settings/profiles");
    await untilQuiet(page);
    const made = await api<Profile>(page, "POST", "/api/quality-profiles", { name, mediaType: "movies", cutoffQuality: "WEB 1080p", allowedQualities: "WEB 720p,WEB 1080p", customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    profileId = made.body.id;
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Uses ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder, qualityProfileId: profileId });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    for (const profile of await profiles(page)) if (profile.name.includes(stamp)) await api(page, "DELETE", `/api/quality-profiles/${profile.id}`);
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("the numbers behind a profile's plain choices stay folded until Show the numbers is opened, and each number field has its own name", async ({ page }) => {
    await page.getByRole("button", { name: "New quality profile" }).first().click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByRole("spinbutton", { name: "Minimum custom-format score to grab" })).toHaveCount(0);
    await drawer.getByRole("button", { name: /Show the numbers/ }).click();
    const fields = drawer.getByRole("spinbutton");
    const names = await fields.evaluateAll((nodes) => nodes.map((node) => node.getAttribute("aria-label") ?? ""));
    expect(names.length).toBeGreaterThanOrEqual(3);
    expect(new Set(names).size).toBe(names.length);
    expect(names.every((label) => label.length > 0)).toBe(true);
  });

  test("a profile's Used by section lists the library that uses it and says the assignment is direct", async ({ page }) => {
    await row(page, name).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText(`Uses ${stamp}`)).toBeVisible();
    await expect(drawer.getByText("direct assignment")).toBeVisible();
    await expect(drawer.getByText("1 library")).toBeVisible();
  });

  test("Duplicate opens a copy named Copy of the profile, saves nothing until Create, and the next copy is numbered", async ({ page }) => {
    const before = (await profiles(page)).length;
    await row(page, name).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("button", { name: "Duplicate", exact: true }).click();
    await expect(drawer.getByLabel("Profile name")).toHaveValue(`Copy of ${name}`);
    expect((await profiles(page)).length).toBe(before);
    await drawer.getByRole("button", { name: "Create quality profile" }).click();
    await expect.poll(async () => (await profiles(page)).length).toBe(before + 1);

    await drawer.getByRole("button", { name: "Cancel" }).click();
    await expect(drawer).toHaveCount(0);
    await page.getByRole("row").filter({ hasText: name }).filter({ hasNotText: "Copy of" }).first().click();
    await page.getByRole("dialog").getByRole("button", { name: "Duplicate", exact: true }).click();
    await expect(page.getByRole("dialog").getByLabel("Profile name")).toHaveValue(`Copy of ${name} (2)`);
  });

  test("Duplicate is off while the open profile has unsaved changes", async ({ page }) => {
    await row(page, name).click();
    const drawer = page.getByRole("dialog");
    const duplicate = drawer.getByRole("button", { name: "Duplicate", exact: true });
    await expect(duplicate).toBeEnabled();
    await drawer.getByLabel("Profile name").fill(`Edited ${stamp}`);
    await expect(duplicate).toBeDisabled();
  });

  test("closing a changed profile drawer asks first, Cancel keeps the edit, and Discard leaves the stored profile as it was", async ({ page }) => {
    await row(page, name).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByPlaceholder("Name this quality profile").fill(`Changed ${stamp}`);
    await page.keyboard.press("Escape");
    const ask = page.getByRole("dialog", { name: "Discard unsaved changes?" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer.getByPlaceholder("Name this quality profile")).toHaveValue(`Changed ${stamp}`);
    await page.keyboard.press("Escape");
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    const names = (await profiles(page)).map((profile) => profile.name);
    expect(names).toContain(name);
    expect(names).not.toContain(`Changed ${stamp}`);
  });
});

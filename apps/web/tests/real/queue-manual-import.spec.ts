import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Manual import's drawer on Transfers against the real server and real folders, nothing stubbed (measure 4, issue 1270): the
 * folder field browses the server (Advanced, Open, Up, Use this folder), Scan folder lists the video files in it with a guessed
 * title, each row can be left out, corrected and checked again, and nothing is imported until Import selected.
 */

const stamp = `mi${Date.now()}`;
let base = "";
let libraryRoot = "";
let libraryId = "";

test.describe("Manual import", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    base = fs.mkdtempSync(path.join(os.tmpdir(), `deluno-real-manualimport-${stamp}-`));
    fs.mkdirSync(path.join(base, "pack", "inner"), { recursive: true });
    fs.writeFileSync(path.join(base, "pack", "Scan.One.2019.1080p.WEB-DL.mkv"), Buffer.alloc(2048, 1));
    fs.writeFileSync(path.join(base, "pack", "inner", "Scan.Two.2020.1080p.WEB-DL.mkv"), Buffer.alloc(2048, 2));
    libraryRoot = fs.mkdtempSync(path.join(os.tmpdir(), `deluno-real-manualimport-lib-${stamp}-`));
    const page = await browser.newPage();
    await openScreen(page, "/queue");
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `Manual import films ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: libraryRoot });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/queue");
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(base, { recursive: true, force: true });
    fs.rmSync(libraryRoot, { recursive: true, force: true });
  });

  async function openFolderMode(page: import("@playwright/test").Page) {
    await openScreen(page, "/queue");
    await untilQuiet(page);
    await page.getByRole("button", { name: "Manual import", exact: true }).click();
    const drawer = page.getByRole("dialog").first();
    await drawer.getByRole("radio", { name: "Scan a folder" }).click();
    return drawer;
  }

  test("the folder field browses the server: Advanced opens the browser at the typed folder, Open and Up move about, and Use this folder fills the field", async ({ page }) => {
    const drawer = await openFolderMode(page);
    const field = drawer.getByPlaceholder("D:/Downloads/Some.Release.2024");
    await field.fill(path.join(base, "pack"));
    await expect(field).toHaveValue(path.join(base, "pack"));

    await drawer.getByRole("button", { name: "Advanced", exact: true }).click();
    const sheet = page.getByRole("dialog").filter({ hasText: "Pick the folder Deluno should use" });
    await expect(sheet).toBeVisible();
    await expect(sheet.getByText("inner", { exact: true })).toBeVisible();
    await sheet.getByRole("button", { name: "Open", exact: true }).first().click();
    await expect(sheet.getByText(path.join(base, "pack", "inner")).first()).toBeVisible();
    await sheet.getByRole("button", { name: "Up", exact: true }).click();
    await expect(sheet.getByText("inner", { exact: true })).toBeVisible();
    await sheet.getByRole("button", { name: "Use this folder", exact: true }).click();
    await expect(sheet).toHaveCount(0);
    // The server hands the folder back in its own form, with the closing separator.
    await expect.poll(async () => (await field.inputValue()).replace(/[\\/]+$/, "")).toBe(path.join(base, "pack"));
  });

  test("Scan folder lists every video file with its guessed title, a row can be left out, corrected and checked again, and Import selected waits until one is included", async ({ page }) => {
    const drawer = await openFolderMode(page);
    await drawer.getByPlaceholder("D:/Downloads/Some.Release.2024").fill(path.join(base, "pack"));

    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Scan folder", exact: true }).click();
    await expect(drawer.getByText("Review every match")).toBeVisible();
    expect(requests.seen.filter((r) => r.method === "POST" && /filesystem/.test(r.path)).length).toBeGreaterThan(0);
    const first = drawer.getByRole("checkbox", { name: "Include Scan.One.2019.1080p.WEB-DL.mkv" });
    const second = drawer.getByRole("checkbox", { name: "Include Scan.Two.2020.1080p.WEB-DL.mkv" });
    await expect(first).toBeChecked();
    await expect(second).toBeChecked();
    await expect(drawer.getByText("2 of 2 selected")).toBeVisible();

    await second.uncheck();
    await expect(drawer.getByText("1 of 2 selected")).toBeVisible();
    await first.uncheck();
    await expect(drawer.getByRole("button", { name: "Import selected", exact: true })).toBeDisabled();
    await first.check();
    await expect(drawer.getByRole("button", { name: "Import selected", exact: true })).toBeEnabled();

    const title = drawer.getByRole("textbox", { name: "Title" }).first();
    await title.fill("Scan One Corrected");
    await expect(title).toHaveValue("Scan One Corrected");
    await drawer.getByRole("spinbutton", { name: "Year" }).first().fill("2018");
    const before = requests.seen.filter((r) => r.method === "POST" && /preview/.test(r.path)).length;
    await drawer.getByRole("button", { name: "Re-check", exact: true }).first().click();
    await expect.poll(() => requests.seen.filter((r) => r.method === "POST" && /preview/.test(r.path)).length).toBeGreaterThan(before);
    requests.stop();
    await expectNoErrorScreen(page);
    // Nothing was imported by looking, correcting or checking again.
    expect(requests.seen.filter((r) => r.method === "POST" && /import\/(jobs|execute)/.test(r.path))).toHaveLength(0);
    expect(fs.existsSync(path.join(base, "pack", "Scan.One.2019.1080p.WEB-DL.mkv"))).toBe(true);
  });

  test("One file takes a typed source path, the media type is kept in the request, and the preview names the file", async ({ page }) => {
    await openScreen(page, "/queue");
    await untilQuiet(page);
    await page.getByRole("button", { name: "Manual import", exact: true }).click();
    const drawer = page.getByRole("dialog").first();
    const source = path.join(base, "pack", "Scan.One.2019.1080p.WEB-DL.mkv");
    await drawer.getByPlaceholder("D:/Downloads/Some.Release.2024").fill(source);
    await drawer.getByRole("radio", { name: "Movies" }).click();
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Preview", exact: true }).click();
    await expect.poll(() => requests.find("POST", /^\/api\/filesystem\/import\/preview$/).length).toBe(1);
    requests.stop();
    const sent = JSON.stringify(requests.find("POST", /^\/api\/filesystem\/import\/preview$/)[0].body);
    expect(sent).toContain("Scan.One.2019.1080p.WEB-DL.mkv");
    expect(sent).toContain("movies");
  });
});

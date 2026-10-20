import fs from "node:fs";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * System > Log files against the real server, nothing stubbed (measure 4, issue 1270). The spec adds old log files of its
 * own, named the way Deluno names them, in the folder the screen names, and then lists, refreshes, downloads and deletes
 * them the way a person would, checking the request, the bytes and the folder. The file for today is the host's, and is
 * refused in words.
 */

const stamp = Date.now();
// Deluno's own names: deluno-<date>.jsonl. A date in 2001 or 2002 is never today's file.
const stampDay = (offset: number) => `2001-${String(1 + (stamp % 11)).padStart(2, "0")}-${String(1 + ((stamp + offset) % 27)).padStart(2, "0")}`;
const nameFor = (offset: number) => `deluno-${stampDay(offset)}.jsonl`;

async function folderOf(page: Page) {
  return (await api<{ path: string }>(page, "GET", "/api/system/log-files/folder")).body.path;
}

async function openLogs(page: Page) {
  await page.reload();
  await untilQuiet(page);
  const logs = page.getByRole("region", { name: "Log files" });
  await logs.scrollIntoViewIfNeeded();
  return logs;
}

test.describe("System > Log files", () => {
  test("lists a log file, Refresh asks again, Open logs folder copies the folder's path, and Download gives back the file's own bytes", async ({ page, context }) => {
    test.setTimeout(90_000);
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    await openScreen(page, "/system");
    const folder = await folderOf(page);
    fs.mkdirSync(folder, { recursive: true });
    const name = nameFor(1);
    const file = path.join(folder, name);
    const body = `{"message":"line one ${stamp}"}\n{"message":"line two"}\n`;
    fs.writeFileSync(file, body);
    try {
      const logs = await openLogs(page);
      await expect(logs.getByText(name, { exact: true })).toBeVisible();

      const requests = recordRequests(page);
      await logs.getByRole("button", { name: "Refresh", exact: true }).click();
      await expect.poll(() => requests.find("GET", /^\/api\/system\/log-files$/).length).toBeGreaterThan(0);
      requests.stop();

      await logs.getByRole("button", { name: "Open logs folder", exact: true }).click();
      await expect(page.getByText("Logs folder path copied").first()).toBeVisible();
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(folder);

      const downloading = page.waitForEvent("download");
      await logs.getByRole("button", { name: `Download ${name}`, exact: true }).click();
      const download = await downloading;
      expect(download.suggestedFilename()).toBe(name);
      expect(fs.readFileSync((await download.path())!, "utf8")).toBe(body);
    } finally {
      fs.rmSync(file, { force: true });
    }
  });

  test("Delete asks first, Cancel keeps the file, Delete file removes it from the folder, and the file still being written is refused in words", async ({ page }) => {
    test.setTimeout(90_000);
    await openScreen(page, "/system");
    const folder = await folderOf(page);
    fs.mkdirSync(folder, { recursive: true });
    const name = nameFor(2);
    const file = path.join(folder, name);
    fs.writeFileSync(file, `{"message":"an old line"}\n`);
    try {
      const logs = await openLogs(page);
      await expect(logs.getByText(name, { exact: true })).toBeVisible();

      await logs.getByRole("button", { name: `Delete ${name}`, exact: true }).click();
      const ask = page.getByRole("alertdialog").or(page.getByRole("dialog")).filter({ hasText: `Delete "${name}"?` });
      await expect(ask).toBeVisible();
      await ask.getByRole("button", { name: "Cancel", exact: true }).click();
      await expect(ask).toHaveCount(0);
      expect(fs.existsSync(file)).toBe(true);

      await logs.getByRole("button", { name: `Delete ${name}`, exact: true }).click();
      const requests = recordRequests(page);
      await ask.getByRole("button", { name: "Delete file", exact: true }).click();
      await expect(page.getByText(`${name} deleted`).first()).toBeVisible();
      requests.stop();
      expect(requests.find("DELETE", /^\/api\/system\/log-files\//)).toHaveLength(1);
      expect(fs.existsSync(file)).toBe(false);
      await expect(logs.getByText(name, { exact: true })).toHaveCount(0);
    } finally {
      fs.rmSync(file, { force: true });
    }

    // Today's file is the one being written; a copy made here under today's name stands in for it, and is refused.
    const today = `deluno-${new Date().toISOString().slice(0, 10)}.jsonl`;
    const todays = path.join(folder, today);
    const existed = fs.existsSync(todays);
    if (!existed) fs.writeFileSync(todays, `{"message":"today"}\n`);
    try {
      const refused = await api(page, "DELETE", `/api/system/log-files/${today}`);
      expect(refused.status).toBe(400);
      expect(JSON.stringify(refused.body)).toContain("still being written to");
      expect(fs.existsSync(todays)).toBe(true);
    } finally {
      if (!existed) fs.rmSync(todays, { force: true });
    }
  });
});

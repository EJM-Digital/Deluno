import { expect, test } from "@playwright/test";
import { api, authHeaders, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * System > Backups and System > Recycle bin against the real server, nothing stubbed (measure 4, issue 1270).
 * Each action asserts the request the server really received, the state it ends in and the words on screen.
 */

interface BackupList { items?: Array<{ id: string; fileName: string }> }

const backupItems = async (page: import("@playwright/test").Page) => {
  const listed = await api<BackupList | Array<{ id: string; fileName: string }>>(page, "GET", "/api/backups");
  expect(listed.status).toBe(200);
  return Array.isArray(listed.body) ? listed.body : (listed.body.items ?? []);
};

test.describe("System > Backups", () => {
  test.describe.configure({ mode: "serial" });

  test("the screen's count and wording match what the server holds", async ({ page }) => {
    await openScreen(page, "/system/backups");
    await untilQuiet(page);
    const held = await backupItems(page);
    const heading = held.length ? `${held.length} backup${held.length === 1 ? "" : "s"}` : "No backups yet";
    await expect(page.getByText(heading, { exact: true })).toBeVisible();
    await expect(page.getByText("Protect the app database, settings, queues, and integration cache before changes.")).toBeVisible();
    if (!held.length) await expect(page.getByText("No backups yet.", { exact: true })).toBeVisible();
  });

  test("Create backup sends one request, the server then holds the backup and the screen lists it with a download", async ({ page }) => {
    await openScreen(page, "/system/backups");
    await untilQuiet(page);
    const before = (await backupItems(page)).length;
    const requests = recordRequests(page);

    await page.getByRole("button", { name: "Create backup", exact: true }).click();
    await expect(page.getByText("Backup created", { exact: true })).toBeVisible();
    requests.stop();

    const created = requests.find("POST", /^\/api\/backups$/);
    expect(created, JSON.stringify(requests.seen)).toHaveLength(1);
    expect(created[0].body).toEqual({ reason: "manual" });
    const after = await backupItems(page);
    expect(after.length).toBe(before + 1);
    await expect(page.getByText(`${after.length} backup${after.length === 1 ? "" : "s"}`, { exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: "Download latest" })).toHaveAttribute("href", /\/api\/backups\/[^/]+\/download$/);
  });

  test("the automatic schedule saves what was chosen, and the server keeps it", async ({ page }) => {
    await openScreen(page, "/system/backups");
    await untilQuiet(page);
    const requests = recordRequests(page);

    await page.getByRole("switch", { name: "Enable scheduled backups" }).click();
    await page.getByLabel("Backup retention count").fill("9");
    await page.getByRole("button", { name: "Save backup schedule", exact: true }).click();
    await expect.poll(() => requests.find("PUT", /^\/api\/backups\/settings$/).length).toBe(1);
    requests.stop();

    const sent = requests.find("PUT", /^\/api\/backups\/settings$/)[0].body as { enabled: boolean; retentionCount: number };
    expect(sent.enabled).toBe(true);
    expect(sent.retentionCount).toBe(9);
    const stored = await api<{ enabled: boolean; retentionCount: number }>(page, "GET", "/api/backups/settings");
    expect(stored.body).toMatchObject({ enabled: true, retentionCount: 9 });
    await expect(page.getByText(/Retains latest 9 backups/)).toBeVisible();

    // Put the schedule back the way it was found: nothing automatic is on by default.
    await page.getByRole("switch", { name: "Enable scheduled backups" }).click();
    await page.getByRole("button", { name: "Save backup schedule", exact: true }).click();
    await expect.poll(async () => (await api<{ enabled: boolean }>(page, "GET", "/api/backups/settings")).body.enabled).toBe(false);
  });

  test("a real backup archive previews as valid, and a file that is not a backup is refused in words", async ({ page }) => {
    await openScreen(page, "/system/backups");
    await untilQuiet(page);
    const held = await backupItems(page);
    expect(held.length, "an earlier test created a backup").toBeGreaterThan(0);
    const archive = await page.request.get(`/api/backups/${held[0].id}/download`, { headers: await authHeaders(page) });
    expect(archive.ok()).toBe(true);
    const zip = await archive.body();
    const requests = recordRequests(page);

    await page.locator('input[type="file"]').setInputFiles({ name: "deluno-backup.zip", mimeType: "application/zip", buffer: zip });
    const preview = page.getByRole("button", { name: "Restore backup", exact: true });
    await expect(preview).toBeEnabled();
    expect(requests.find("POST", /^\/api\/backups\/restore\/preview$/)).toHaveLength(1);
    await expect(page.getByText(/\d+ files/)).toBeVisible();
    // Restore replaces Deluno's own data files, so the person's two-step flow stops here: nothing was restored.
    expect(requests.find("POST", /^\/api\/backups\/restore$/)).toHaveLength(0);

    const refused = page.waitForResponse((r) => r.url().endsWith("/api/backups/restore/preview"));
    await page.locator('input[type="file"]').setInputFiles({ name: "not-a-backup.zip", mimeType: "application/zip", buffer: Buffer.from("this is not a zip archive") });
    const answer = (await (await refused).json()) as { valid: boolean; message: string };
    requests.stop();
    expect(requests.find("POST", /^\/api\/backups\/restore\/preview$/)).toHaveLength(2);
    expect(answer.valid).toBe(false);
    expect(answer.message.length).toBeGreaterThan(10);
    await expect(page.getByText(answer.message, { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Restore backup", exact: true })).toBeDisabled();
    expect(requests.find("POST", /^\/api\/backups\/restore$/)).toHaveLength(0);
  });
});

test.describe("System > Recycle bin", () => {
  test.describe.configure({ mode: "serial" });

  test("an empty recycle bin says so in words, and the server agrees", async ({ page }) => {
    await openScreen(page, "/system/recycle-bin");
    await untilQuiet(page);
    const held = await api<unknown[] | { items?: unknown[] }>(page, "GET", "/api/recycle-bin");
    expect(held.status).toBe(200);
    const items = Array.isArray(held.body) ? held.body : (held.body.items ?? []);
    expect(items).toEqual([]);
    await expect(page.getByText("Nothing stored", { exact: true })).toBeVisible();
    await expect(page.getByText("Recycle bin is empty", { exact: true })).toBeVisible();
    await expect(page.getByText("Library removals and replaced files stay recoverable until their retention window expires.")).toBeVisible();
  });

  test("Clean expired items asks the server what it would take, says so in words, and removes nothing", async ({ page }) => {
    await openScreen(page, "/system/recycle-bin");
    await untilQuiet(page);
    const requests = recordRequests(page);

    await page.getByRole("button", { name: "Clean expired items", exact: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Nothing is ready to be emptied", { exact: true })).toBeVisible();
    expect(requests.find("GET", /^\/api\/recycle-bin\/cleanup\/preview$/)).toHaveLength(1);
    await dialog.getByRole("button", { name: "Close", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    requests.stop();
    expect(requests.find("POST", /^\/api\/recycle-bin\/cleanup$/), "closing the dialog does not empty anything").toHaveLength(0);
  });

  test("the retention settings save what was typed and the server keeps it", async ({ page }) => {
    await openScreen(page, "/system/recycle-bin");
    await untilQuiet(page);
    const before = await api<{ retentionDays: number; maxSizeMb: number }>(page, "GET", "/api/recycle-bin/settings");
    expect(before.status).toBe(200);
    const requests = recordRequests(page);

    await page.getByLabel("Retention days").fill("45");
    await page.getByRole("button", { name: "Save recycle-bin settings", exact: true }).click();
    await expect.poll(() => requests.seen.filter((r) => r.method === "PUT" && r.path.startsWith("/api/recycle-bin")).length).toBe(1);
    requests.stop();
    const put = requests.seen.filter((r) => r.method === "PUT" && r.path.startsWith("/api/recycle-bin"))[0];
    expect(put.body).toMatchObject({ retentionDays: 45 });
    const stored = await api<{ retentionDays: number }>(page, "GET", "/api/recycle-bin/settings");
    expect(stored.body.retentionDays).toBe(45);

    await api(page, "PUT", "/api/recycle-bin/settings", before.body);
  });
});

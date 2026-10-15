import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1294: the System screens that change files, run against the real host with nothing stubbed. Each test clicks
// the real button and then looks at what really happened: the zip on disk, the file in the recycle bin, the key
// that stops working. Nothing here uses page.route.

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

interface BackupRow { id: string; fileName: string; sizeBytes: number }
interface BinRow { id: string; originalPath: string; recyclePath: string }

test.describe("System screens on the real host (#1294)", () => {
  test("Backups: create, list, the newest is a real zip, save the schedule, preview and restore", async ({ page }) => {
    test.setTimeout(120_000);
    await authenticateAndNavigate(page, "/system/backups");
    const headers = await authHeaders(page);
    const listBackups = async () => (await (await page.request.get("/api/backups", { headers })).json()) as BackupRow[];
    const before = await listBackups();

    // "Download latest" appears only when there is a backup to download.
    if (before.length === 0) {
      await expect(page.getByText("No backups yet.")).toBeVisible();
      await expect(page.getByRole("link", { name: "Download latest" })).toHaveCount(0);
    }

    await page.getByRole("button", { name: "Create backup" }).click();
    await expect(page.getByText("Backup created")).toBeVisible({ timeout: 30_000 });
    const after = await listBackups();
    expect(after.length).toBe(before.length + 1);
    const made = after.find((row) => !before.some((old) => old.id === row.id))!;
    await expect(page.getByText(made.fileName, { exact: true })).toBeVisible();

    // The newest backup downloads as a real zip.
    const latest = page.getByRole("link", { name: "Download latest" });
    await expect(latest).toHaveAttribute("href", `/api/backups/${after[0]!.id}/download`);
    const bytes = await (await page.request.get(`/api/backups/${made.id}/download`, { headers })).body();
    expect(bytes.subarray(0, 2).toString("latin1"), "a backup is a zip file").toBe("PK");
    expect(bytes.length).toBe(made.sizeBytes);

    // The schedule is saved to the server and survives a reload.
    const settingsBefore = (await (await page.request.get("/api/backups/settings", { headers })).json()) as { retentionCount: number; enabled: boolean };
    const retention = page.getByLabel("Backup retention count");
    const changed = settingsBefore.retentionCount === 11 ? 12 : 11;
    await retention.fill(String(changed));
    await page.getByRole("button", { name: "Save backup schedule" }).click();
    await expect(page.getByText("Saved just now")).toBeVisible();
    expect(((await (await page.request.get("/api/backups/settings", { headers })).json()) as { retentionCount: number }).retentionCount).toBe(changed);
    await page.reload();
    await expect(page.getByLabel("Backup retention count")).toHaveValue(String(changed));
    // Put it back the way it was.
    await page.getByLabel("Backup retention count").fill(String(settingsBefore.retentionCount));
    await page.getByRole("button", { name: "Save backup schedule" }).click();
    await expect(page.getByText("Saved just now")).toBeVisible();

    // Choosing a zip uploads it for a preview by itself.
    const previewRequest = page.waitForRequest((request) => request.url().endsWith("/api/backups/restore/preview") && request.method() === "POST");
    await page.getByLabel("Backup archive").setInputFiles({ name: made.fileName, mimeType: "application/zip", buffer: bytes });
    await previewRequest;
    const restoreButton = page.getByRole("button", { name: "Restore backup" });
    await expect(restoreButton).toBeEnabled({ timeout: 30_000 });

    // Restore asks first, and Cancel does nothing.
    const restoreRequests: string[] = [];
    page.on("request", (request) => { if (request.url().endsWith("/api/backups/restore") && request.method() === "POST") restoreRequests.push(request.url()); });
    await restoreButton.click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Restore this backup?")).toBeVisible();
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toHaveCount(0);
    expect(restoreRequests).toHaveLength(0);

    // Confirming uploads the archive and the server says what to do next.
    await restoreButton.click();
    await page.getByRole("dialog").getByRole("button", { name: "Restore now" }).click();
    await expect(page.getByText(/Restart Deluno/i).first()).toBeVisible({ timeout: 60_000 });
    expect(restoreRequests).toHaveLength(1);
  });

  // #1331: the Backups page offers "Download latest" and a "Download" link per backup. A followed link sends no Authorization header, so the server
  // answered 401 and nothing was saved (DOWNLOAD-LINK status=401 downloadEvent=none sentAuthorization=null). The links now fetch with the sign-in and
  // save the result.
  test("Backups: the Download latest link downloads the newest zip, fetched with the sign-in", async ({ page }) => {
    await authenticateAndNavigate(page, "/system/backups");
    const headers = await authHeaders(page);
    const made = await page.request.post("/api/backups", { headers, data: { reason: "manual" } });
    expect(made.ok()).toBe(true);
    await page.reload();
    const link = page.getByRole("link", { name: "Download latest" });
    await expect(link).toBeVisible();
    const answer = page.waitForResponse((response) => /\/api\/backups\/[^/]+\/download$/.test(response.url()), { timeout: 15_000 }).catch(() => null);
    const download = page.waitForEvent("download", { timeout: 15_000 }).catch(() => null);
    await link.click();
    const [response, file] = await Promise.all([answer, download]);
    console.log(`DOWNLOAD-LINK status=${response?.status()} downloadEvent=${file ? file.suggestedFilename() : "none"} signedIn=${Boolean(response?.request().headers().authorization)}`);
    expect(file, `the link answered ${response?.status()} and no file was saved`).not.toBeNull();
    expect(response?.status()).toBe(200);
    expect(response?.request().headers().authorization).toMatch(/^Bearer /);
    expect(file!.suggestedFilename()).toMatch(/\.zip$/);
  });

  test("Backups: a file that is not a backup is refused at the preview and cannot be restored", async ({ page }) => {
    await authenticateAndNavigate(page, "/system/backups");
    const previewAnswer = page.waitForResponse((response) => response.url().endsWith("/api/backups/restore/preview"));
    await page.getByLabel("Backup archive").setInputFiles({ name: "not-a-backup.zip", mimeType: "application/zip", buffer: Buffer.from("this is not a zip file") });
    await previewAnswer;
    await expect(page.getByText(/valid|manifest|backup|zip|archive/i).filter({ hasText: /not|invalid|could|cannot|missing/i }).first()).toBeVisible();
    const restore = page.getByRole("button", { name: "Restore backup" });
    if (await restore.count()) await expect(restore).toBeDisabled();
  });

  test("Recycle bin: delete permanently asks first and the files really go, and with nothing expired there is nothing to clean", async ({ page }) => {
    test.setTimeout(120_000);
    await authenticateAndNavigate(page, "/system/recycle-bin");
    const headers = await authHeaders(page);

    const root = mkdtempSync(path.join(os.tmpdir(), "deluno-bin-real-"));
    const stamp = Date.now();
    const library = await (await page.request.post("/api/libraries", {
      headers,
      data: { name: `Bin-real-${stamp}`, mediaType: "movies", purpose: "collection", rootPath: root, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false }
    })).json() as { id: string };
    const settingsBefore = (await (await page.request.get("/api/recycle-bin/settings", { headers })).json()) as { retentionDays: number; maxSizeMb: number };
    const movieIds: string[] = [];
    const binRows = async () => (await (await page.request.get("/api/recycle-bin", { headers })).json()) as BinRow[];

    try {
      const files: string[] = [];
      const folders: string[] = [];
      for (const n of [1, 2]) {
        const title = `Bin Film ${stamp} ${n}`;
        const folder = path.join(root, `${title} (200${n})`);
        mkdirSync(folder, { recursive: true });
        const file = path.join(folder, `${title.replace(/ /g, ".")}.200${n}.1080p.BluRay.x264-GRP.mkv`);
        writeFileSync(file, Buffer.alloc(1_572_864, n));
        files.push(file);
        folders.push(folder);
        const created = await page.request.post("/api/movies/", { headers, data: { title, releaseYear: 2000 + n, monitored: true, libraryId: library.id } });
        expect(created.ok()).toBe(true);
        const movie = (await created.json()) as { id: string };
        movieIds.push(movie.id);
        await expect.poll(async () => ((await (await page.request.get(`/api/movies/${movie.id}`, { headers })).json()) as { hasFile?: boolean }).hasFile).toBe(true);
        const removed = await page.request.delete(`/api/movies/${movie.id}?deleteFiles=true`, { headers });
        expect(removed.ok(), `remove ${title}: ${removed.status()}`).toBe(true);
        expect(existsSync(file), "the file left the library").toBe(false);
      }

      await page.reload();
      // The title leaves as its own folder: the bin holds the folder, with the file inside it.
      const same = (a: string, b: string) => path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
      const mine = (await binRows()).filter((row) => folders.some((folder) => same(folder, row.originalPath)));
      expect(mine).toHaveLength(2);
      for (const row of mine) {
        expect(existsSync(row.recyclePath), "the file is in the recycle bin on disk").toBe(true);
        await expect(page.getByText(row.originalPath, { exact: true })).toBeVisible();
      }

      // Delete permanently: asks first, Cancel keeps the item, confirming removes the stored file.
      const first = mine.find((row) => same(row.originalPath, folders[0]!))!;
      const second = mine.find((row) => same(row.originalPath, folders[1]!))!;
      const firstItem = page.locator("div.rounded-xl").filter({ has: page.getByText(first.originalPath, { exact: true }) }).last();
      await firstItem.getByRole("button", { name: "Delete permanently" }).click();
      const dialog = page.getByRole("dialog");
      await expect(dialog.getByText("Delete this item permanently?")).toBeVisible();
      await dialog.getByRole("button", { name: "Cancel" }).click();
      await expect(dialog).toHaveCount(0);
      expect(existsSync(first.recyclePath), "Cancel keeps the stored file").toBe(true);
      await expect(page.getByText(first.originalPath, { exact: true })).toBeVisible();

      await firstItem.getByRole("button", { name: "Delete permanently" }).click();
      await page.getByRole("dialog").getByRole("button", { name: "Delete permanently" }).click();
      await expect(page.getByText(first.originalPath, { exact: true })).toHaveCount(0);
      expect(existsSync(first.recyclePath), "confirming removes the stored file").toBe(false);
      expect(existsSync(second.recyclePath), "the other item is untouched").toBe(true);

      // Nothing has expired and nothing is over capacity: the dialog says so, and closing it takes nothing.
      await page.getByRole("button", { name: "Clean expired items" }).click();
      const nothing = page.getByRole("dialog");
      await expect(nothing.getByText("Nothing is ready to be emptied")).toBeVisible();
      await nothing.getByRole("button", { name: "Close" }).click();
      expect(existsSync(second.recyclePath)).toBe(true);

      // The retention field is clamped by the server to a year.
      await page.getByLabel("Retention days").fill("400");
      await page.getByRole("button", { name: "Save recycle-bin settings" }).click();
      await expect(page.getByText("Recycle-bin settings saved.").first()).toBeVisible();
      await expect.poll(async () => ((await (await page.request.get("/api/recycle-bin/settings", { headers })).json()) as { retentionDays: number }).retentionDays).toBe(365);
    } finally {
      await page.request.put("/api/recycle-bin/settings", { headers, data: settingsBefore });
      for (const id of movieIds) await page.request.delete(`/api/movies/${id}`, { headers });
      await page.request.delete(`/api/libraries/${library.id}`, { headers });
      rmSync(root, { recursive: true, force: true });
    }
  });

  test("API keys: generate, use, ask before revoking, and a revoked key stops working", async ({ page, request }) => {
    await authenticateAndNavigate(page, "/system/api");
    const name = `Probe key ${Date.now()}`;

    await page.getByLabel("Key name").fill(name);
    await page.getByRole("button", { name: "Generate API key" }).click();
    await expect(page.getByText("one-time secret")).toBeVisible();
    const key = (await page.locator("code").filter({ hasText: /^deluno_/ }).first().textContent())!.trim();
    expect(key).toMatch(/^deluno_/);
    const works = async () => (await request.get("/api/system/health", { headers: { "X-Api-Key": key } })).status();
    expect(await works(), "the new key signs a request in").toBe(200);

    const row = page.getByRole("row").filter({ hasText: name });
    await expect(row).toHaveCount(1);
    await row.getByRole("button", { name: "Revoke" }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText(`Revoke "${name}"?`)).toBeVisible();
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toHaveCount(0);
    await expect(row).toHaveCount(1);
    expect(await works(), "Cancel keeps the key working").toBe(200);

    await row.getByRole("button", { name: "Revoke" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Revoke key" }).click();
    await expect(page.getByText("API key revoked").first()).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: name })).toHaveCount(0);
    await expect(page.getByText("one-time secret")).toHaveCount(0);
    expect(await works(), "a revoked key no longer signs a request in").toBe(401);
  });

  test("Activity timeline: Clear asks first, names the history it removes, and Cancel leaves everything", async ({ page }) => {
    await authenticateAndNavigate(page, "/system/audit");
    const headers = await authHeaders(page);
    const count = async () => ((await (await page.request.get("/api/activity?pageSize=1000", { headers })).json()) as { items: unknown[] }).items.length;
    const before = await count();

    await page.getByRole("button", { name: "Clear", exact: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Clear the Activity timeline?")).toBeVisible();
    await expect(dialog.getByText(/including the history behind automation decision explanations/)).toBeVisible();
    await expect(dialog.getByText(/This cannot be undone/)).toBeVisible();
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toHaveCount(0);

    expect(await count(), "Cancel leaves every row").toBeGreaterThanOrEqual(before);
  });

  test("System tasks: Check files now finds a file that left the disk and the title is marked missing", async ({ page }) => {
    test.setTimeout(120_000);
    await authenticateAndNavigate(page, "/system");
    const headers = await authHeaders(page);
    const root = mkdtempSync(path.join(os.tmpdir(), "deluno-filecheck-real-"));
    const stamp = Date.now();
    const library = await (await page.request.post("/api/libraries", {
      headers,
      data: { name: `Check-real-${stamp}`, mediaType: "movies", purpose: "collection", rootPath: root, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false }
    })).json() as { id: string };
    const movieIds: string[] = [];
    const files: string[] = [];
    try {
      for (const n of [1, 2, 3, 4]) {
        const title = `Check Film ${stamp} ${n}`;
        const folder = path.join(root, `${title} (201${n})`);
        mkdirSync(folder, { recursive: true });
        const file = path.join(folder, `${title.replace(/ /g, ".")}.201${n}.1080p.BluRay.x264-GRP.mkv`);
        writeFileSync(file, `film ${n}`);
        files.push(file);
        const created = await page.request.post("/api/movies/", { headers, data: { title, releaseYear: 2010 + n, monitored: true, libraryId: library.id } });
        expect(created.ok()).toBe(true);
        const movie = (await created.json()) as { id: string };
        movieIds.push(movie.id);
        await expect.poll(async () => ((await (await page.request.get(`/api/movies/${movie.id}`, { headers })).json()) as { hasFile?: boolean }).hasFile).toBe(true);
      }
      rmSync(files[0]!);

      await page.reload();
      await page.getByRole("button", { name: "Check files now" }).click();

      await expect(page.getByText(/\d+ titles? (is|are) now missing|Every file Deluno is holding is still on disk/).first()).toBeVisible({ timeout: 30_000 });
      await expect(page.getByText(/\d+ titles? (is|are) now missing/).first()).toBeVisible();
      const gone = (await (await page.request.get(`/api/movies/${movieIds[0]}`, { headers })).json()) as { hasFile?: boolean };
      expect(gone.hasFile, "the title whose file left the disk is marked missing").toBe(false);
      for (const id of movieIds.slice(1)) {
        expect(((await (await page.request.get(`/api/movies/${id}`, { headers })).json()) as { hasFile?: boolean }).hasFile, "a file still on disk stays").toBe(true);
      }
      for (const file of files.slice(1)) expect(existsSync(file), "the check deletes nothing").toBe(true);
    } finally {
      for (const id of movieIds) await page.request.delete(`/api/movies/${id}`, { headers });
      await page.request.delete(`/api/libraries/${library.id}`, { headers });
      rmSync(root, { recursive: true, force: true });
    }
  });

  test("Health: These files really are gone asks first, says nothing is deleted from the disk, and marks only what is missing", async ({ page }) => {
    test.setTimeout(180_000);
    await authenticateAndNavigate(page, "/system");
    const headers = await authHeaders(page);
    const root = mkdtempSync(path.join(os.tmpdir(), "deluno-gone-real-"));
    const stamp = Date.now();
    const libraryName = `Gone-real-${stamp}`;
    const library = await (await page.request.post("/api/libraries", {
      headers,
      data: { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: root, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false }
    })).json() as { id: string };
    const films = 24;
    const lost = 22;
    const movieIds: string[] = [];
    const files: string[] = [];
    const hasFile = async (id: string) => ((await (await page.request.get(`/api/movies/${id}`, { headers })).json()) as { hasFile?: boolean }).hasFile;
    try {
      for (let n = 0; n < films; n++) {
        const title = `Gone Film ${stamp} ${String.fromCharCode(65 + n)}`;
        const year = 1950 + n;
        const folder = path.join(root, `${title} (${year})`);
        mkdirSync(folder, { recursive: true });
        const file = path.join(folder, `${title.replace(/ /g, ".")}.${year}.1080p.BluRay.x264-GRP.mkv`);
        writeFileSync(file, `film ${n}`);
        files.push(file);
        const created = await page.request.post("/api/movies/", { headers, data: { title, releaseYear: year, monitored: true, libraryId: library.id } });
        expect(created.ok()).toBe(true);
        movieIds.push(((await created.json()) as { id: string }).id);
      }
      await expect.poll(() => hasFile(movieIds[films - 1]!)).toBe(true);
      for (const file of files.slice(0, lost)) rmSync(file);
      const check = await page.request.post("/api/filesystem/file-check", { headers });
      expect(check.ok()).toBe(true);
      for (const id of movieIds) expect(await hasFile(id), "one pass changes nothing when a big share is missing at once").toBe(true);

      await page.reload();
      const button = page.getByRole("button", { name: `These files really are gone: ${libraryName} looks unreachable` });
      await expect(button).toBeVisible({ timeout: 30_000 });
      const goneRequests: string[] = [];
      page.on("request", (request) => { if (request.url().includes("/files-gone") && request.method() === "POST") goneRequests.push(request.url()); });

      // It asks first, and says nothing is deleted from the disk and how many files.
      await button.click();
      const dialog = page.getByRole("dialog");
      await expect(dialog.getByText("Mark these files as gone?")).toBeVisible();
      await expect(dialog.getByText(/Nothing is deleted from your disk/)).toBeVisible();
      await expect(dialog.getByText(new RegExp(`about ${lost} files`))).toBeVisible();
      await dialog.getByRole("button", { name: "Cancel" }).click();
      await expect(dialog).toHaveCount(0);
      expect(goneRequests, "nothing is posted before the person confirms").toHaveLength(0);
      expect(await hasFile(movieIds[0]!)).toBe(true);

      // Confirming marks what is missing, leaves what is there, and touches no file.
      await button.click();
      await page.getByRole("dialog").getByRole("button", { name: "Mark them as gone" }).click();
      await expect.poll(() => goneRequests.length).toBe(1);
      await expect.poll(() => hasFile(movieIds[0]!), { timeout: 30_000 }).toBe(false);
      for (const id of movieIds.slice(0, lost)) expect(await hasFile(id), "a missing file is marked gone").toBe(false);
      for (const id of movieIds.slice(lost)) expect(await hasFile(id), "a file still on disk is left alone").toBe(true);
      for (const file of files.slice(lost)) expect(existsSync(file), "Deluno deletes nothing").toBe(true);
      await expect(button).toHaveCount(0, { timeout: 30_000 });
    } finally {
      for (const id of movieIds) await page.request.delete(`/api/movies/${id}`, { headers });
      await page.request.delete(`/api/libraries/${library.id}`, { headers });
      rmSync(root, { recursive: true, force: true });
    }
  });

  test("Allowed Hosts: turning it on makes the real host refuse an unlisted address, keeps the listed one and this machine, and turning it off lets everything back in", async ({ page, request }) => {
    await authenticateAndNavigate(page, "/system/security");
    const headers = await authHeaders(page);
    // Straight to the host itself, so the Host header each request carries is the one the test chooses.
    const api = `http://127.0.0.1:${process.env.DELUNO_SMOKE_API_PORT ?? "5199"}`;
    const call = (hostName: string | null) => request.get(`${api}/api/system/health`, { headers: { ...headers, ...(hostName ? { Host: hostName } : {}) }, failOnStatusCode: false });
    const put = (enabled: boolean, allowedHosts: string[]) =>
      request.put(`${api}/api/security/allowed-hosts`, { headers, data: { enabled, allowedHosts }, failOnStatusCode: false });

    expect((await call("evil.example")).status(), "off: any address is answered").toBe(200);
    try {
      expect((await put(true, ["deluno.home.example"])).status()).toBe(200);
      await expect.poll(async () => (await call("evil.example")).status(), { message: "an unlisted address is refused" }).toBe(400);
      expect((await call("deluno.home.example")).status(), "the listed address is answered").toBe(200);
      expect((await call(null)).status(), "this machine is never locked out").toBe(200);
      expect((await call("localhost")).status(), "localhost is always allowed").toBe(200);
    } finally {
      expect((await put(false, [])).status()).toBe(200);
    }
    await expect.poll(async () => (await call("evil.example")).status(), { message: "off again: any address is answered" }).toBe(200);
  });

  test("/system/docs opens the Help & Guides page", async ({ page }) => {
    await authenticateAndNavigate(page, "/system/docs");
    await expect(page.getByText("Deluno Operating Model")).toBeVisible();
    await expect(page.getByRole("link", { name: "Open" }).first()).toHaveAttribute("href", "/setup-guide");
  });
});

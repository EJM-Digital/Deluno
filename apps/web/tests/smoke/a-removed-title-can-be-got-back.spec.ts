import { mkdirSync, mkdtempSync, existsSync, writeFileSync, rmSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

async function authHeaders(page: import("@playwright/test").Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/**
 * #1160 and #1161. The remove dialog said deleted files "cannot be undone"
 * while Deluno moves them to its recycle bin, and Restore brought the folder
 * back but not the title. Now the dialog says where the files go and for how
 * long, the toast offers Restore, and Restore brings back the title with its
 * file.
 */
test("a movie removed with its files says where they went and comes back whole", async ({ page }) => {
  await authenticateAndNavigate(page, "/movies");
  const headers = await authHeaders(page);

  const root = mkdtempSync(path.join(os.tmpdir(), "deluno-removed-title-"));
  const stamp = Date.now();
  const title = `Restore Film ${stamp}`;
  const folder = path.join(root, `${title} (2000)`);
  mkdirSync(folder, { recursive: true });
  const file = path.join(folder, `${title.replace(/ /g, ".")}.2000.1080p.BluRay.x264-GRP.mkv`);
  writeFileSync(file, "not really a film");

  const libraryResponse = await page.request.post("/api/libraries", {
    headers,
    data: {
      name: `Removed-title-${stamp}`, mediaType: "movies", purpose: "collection", rootPath: root,
      autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false
    }
  });
  expect(libraryResponse.ok(), `POST /api/libraries failed: ${libraryResponse.status()}`).toBe(true);
  const library = await libraryResponse.json() as { id: string };

  try {
    // Adding a title whose folder already holds its file matches the file.
    const create = await page.request.post("/api/movies/", {
      headers,
      data: { title, releaseYear: 2000, monitored: true, libraryId: library.id }
    });
    expect(create.ok()).toBe(true);
    const movie = await create.json() as { id: string };
    await expect.poll(async () => {
      const response = await page.request.get(`/api/movies/${movie.id}`, { headers });
      return (await response.json() as { hasFile?: boolean }).hasFile;
    }).toBe(true);

    await page.goto(`/movies/${movie.id}`);
    await page.getByRole("button", { name: "Remove from Deluno" }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("Deluno's recycle bin");
    await expect(dialog).toContainText(/kept for \d+ days?/);
    await expect(dialog).not.toContainText("cannot be undone");
    await expect(dialog.getByRole("link", { name: "Open the recycle bin" })).toBeVisible();

    await dialog.getByRole("checkbox", { name: /Move imported files to the recycle bin/ }).click();
    await dialog.getByRole("button", { name: "Remove movie", exact: true }).click();
    await expect(page).toHaveURL(/\/movies$/);
    expect(existsSync(file)).toBe(false);
    expect((await page.request.get(`/api/movies/${movie.id}`, { headers })).status()).toBe(404);

    // The toast says where the files went and offers Restore.
    await expect(page.getByText(/recycle bin/i).first()).toBeVisible();
    await page.getByRole("button", { name: "Restore", exact: true }).click();

    await expect(page).toHaveURL(new RegExp(`/movies/${movie.id}$`));
    expect(existsSync(file)).toBe(true);
    const back = await page.request.get(`/api/movies/${movie.id}`, { headers });
    expect(back.status()).toBe(200);
    expect((await back.json() as { hasFile?: boolean }).hasFile).toBe(true);
  } finally {
    await page.request.delete(`/api/movies/${(await (await page.request.get(`/api/movies/page?search=${encodeURIComponent(title)}`, { headers })).json() as { items: Array<{ id: string }> }).items[0]?.id ?? "none"}`, { headers });
    await page.request.delete(`/api/libraries/${library.id}`, { headers });
    rmSync(root, { recursive: true, force: true });
  }
});

test("the recycle bin page shows only the recycle bin", async ({ page }) => {
  await authenticateAndNavigate(page, "/system/recycle-bin");
  await expect(page.getByText("Recycle bin", { exact: false }).first()).toBeVisible();
  await expect(page.getByText("This installation", { exact: true })).toHaveCount(0);
  await expect(page.getByText(".NET runtime")).toHaveCount(0);
});

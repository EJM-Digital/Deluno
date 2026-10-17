import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * System > Health, the rows for stray items in a processor's output folder, against the real server, nothing stubbed
 * (measure 4, issue 1294). A real folder with a real stray media file and a real scrap file is set up through the real
 * library route; the screen lists them; Import (media only) lands on Transfers with the Manual import drawer open and the
 * path and media type already filled in; Clear moves the item to the recycle bin and the row goes.
 */

test.describe("System > Health, processor output", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(150_000);

  let folder = "";
  let output = "";

  test.beforeAll(() => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-health-output-"));
    output = path.join(folder, "Refined");
    fs.mkdirSync(path.join(folder, "Films"));
    fs.mkdirSync(output);
    for (const name of ["Stray.Output.Film.2021.1080p.mkv", "stray-notes.nfo"]) {
      const file = path.join(output, name);
      fs.writeFileSync(file, Buffer.alloc(2048, 1));
      const old = new Date(Date.now() - 30 * 60 * 1000);
      fs.utimesSync(file, old, old);
    }
  });

  test.afterAll(() => {
    fs.rmSync(folder, { recursive: true, force: true });
  });

  test("a stray media file offers Import and a file that is not media does not; Import opens Manual import with the path filled in", async ({ page }) => {
    await openScreen(page, "/system");
    const created = await api(page, "POST", "/api/libraries", {
      name: "Real health output films", mediaType: "movies", purpose: "collection", rootPath: path.join(folder, "Films"),
      importWorkflow: "refine-before-import", processorName: "Weir", processorOutputPath: output
    });
    expect(created.status, JSON.stringify(created.body)).toBeLessThan(300);
    expect((await api(page, "POST", "/api/system/health/run")).status).toBeLessThan(300);

    // "Check now" asks the scheduler to run every check again; the rows arrive within seconds.
    await expect.poll(async () => {
      const listed = await api<{ checks: Array<{ id: string }> }>(page, "GET", "/api/system/health");
      return listed.body.checks.filter((check) => check.id.startsWith("processor-output:")).length;
    }, { timeout: 90_000, intervals: [1000, 2000, 3000] }).toBe(2);

    await page.reload();
    await untilQuiet(page);
    const film = page.getByRole("row").filter({ hasText: "Stray.Output.Film.2021.1080p.mkv" });
    await expect(film).toBeVisible({ timeout: 60_000 });
    const scraps = page.getByRole("row").filter({ hasText: "stray-notes.nfo" });
    await expect(scraps).toBeVisible();
    await expect(film.getByRole("button", { name: "Import", exact: true })).toBeVisible();
    await expect(scraps.getByRole("button", { name: "Import", exact: true })).toHaveCount(0);
    await expect(scraps.getByRole("button", { name: /^Clear / })).toBeVisible();

    await film.getByRole("button", { name: "Import", exact: true }).click();

    await expect(page).toHaveURL(/\/queue/);
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Bring in files Deluno did not download itself")).toBeVisible();
    await expect(drawer.getByPlaceholder("D:/Downloads/Some.Release.2024")).toHaveValue(path.join(output, "Stray.Output.Film.2021.1080p.mkv"));
    await expect(drawer.getByRole("radio", { name: "One file" })).toBeChecked();
    await expectNoErrorScreen(page);
    expect(fs.existsSync(path.join(output, "Stray.Output.Film.2021.1080p.mkv")), "opening Manual import changes nothing on disk").toBe(true);
  });

  test("Clear on a stray item moves it to the recycle bin, leaves the other item and the library, and the row goes", async ({ page }) => {
    await openScreen(page, "/system");
    await untilQuiet(page);
    const scraps = page.getByRole("row").filter({ hasText: "stray-notes.nfo" });
    await expect(scraps).toBeVisible({ timeout: 60_000 });

    await scraps.getByRole("button", { name: /^Clear / }).click();

    await expect.poll(() => fs.existsSync(path.join(output, "stray-notes.nfo"))).toBe(false);
    expect(fs.existsSync(path.join(output, "Stray.Output.Film.2021.1080p.mkv")), "the other item is untouched").toBe(true);
    expect(fs.existsSync(path.join(folder, "Films")), "the library folder is untouched").toBe(true);
    const bin = await api<Array<{ originalPath?: string }> | { items?: Array<{ originalPath?: string }> }>(page, "GET", "/api/recycle-bin");
    const items = Array.isArray(bin.body) ? bin.body : (bin.body.items ?? []);
    expect(items.some((item) => (item.originalPath ?? "").includes("stray-notes.nfo")), JSON.stringify(bin.body)).toBe(true);
    await expect(page.getByRole("row").filter({ hasText: "stray-notes.nfo" })).toHaveCount(0, { timeout: 60_000 });
  });
});

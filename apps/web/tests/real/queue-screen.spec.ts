import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Transfers (the Queue screen) against the real server, nothing stubbed (measure 4, issue 1270): the empty state,
 * the safeguards it states, and Manual import with a real file on this machine's disk.
 * A populated queue needs a download client with a real download in it, which this disposable host does not have:
 * that state is proven on the golden VM scenarios, not here.
 */

test.describe("Transfers", () => {
  test("with nothing sent to a download client the screen says so in words and every counter is there", async ({ page }) => {
    await openScreen(page, "/queue");
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    await expect(page.getByText("Nothing in motion", { exact: true })).toBeVisible();
    await expect(page.getByText("Media appears here from the moment Deluno sends it to a download client through processing, naming, and import.")).toBeVisible();
    await expect(page.getByText("Deluno only shows downloads it sent.")).toBeVisible();
    const text = await page.locator("main").innerText();
    for (const word of ["DOWNLOADING", "PROCESSING", "IMPORTING", "READY TO IMPORT", "NEEDS ACTION", "CLIENTS"]) {
      expect(text).toContain(word);
    }
    expect(text).toContain("all clear");
    expect(text).toContain("nothing waiting");
    await expect(page.getByRole("link", { name: /See everything in Activity/ })).toHaveAttribute("href", "/activity");
  });

  test("the cleanup safeguards read Off on a fresh install, as nothing is automatic until chosen", async ({ page }) => {
    await openScreen(page, "/queue");
    await untilQuiet(page);
    const text = await page.locator("main").innerText();
    for (const label of ["BLOCK RELEASE", "QUEUE REPLACEMENT", "REMOVE CLIENT ENTRY", "PURGE PAYLOAD"]) {
      expect(text).toMatch(new RegExp(`${label}\\s+Off`));
    }
  });

  test("Manual import opens a drawer, offers one file or a folder, and a real file previews through the server", async ({ page }) => {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-import-"));
    const file = path.join(folder, "Real.Import.Film.2021.1080p.WEB-DL.mkv");
    fs.writeFileSync(file, Buffer.alloc(2048, 1));
    try {
      await openScreen(page, "/queue");
      await untilQuiet(page);
      await page.getByRole("button", { name: "Manual import", exact: true }).click();
      const drawer = page.getByRole("dialog");
      await expect(drawer.getByText("Bring in files Deluno did not download itself")).toBeVisible();
      await expect(drawer.getByRole("radio", { name: "One file" })).toBeChecked();
      await expect(drawer.getByText("Preview first — it shows the destination path and the new name without writing anything.")).toBeVisible();
      await expect(drawer.getByRole("button", { name: "Preview", exact: true })).toBeDisabled();

      const requests = recordRequests(page);
      await drawer.getByPlaceholder("D:/Downloads/Some.Release.2024").fill(file);
      await drawer.getByRole("button", { name: "Preview", exact: true }).click();
      await expect.poll(() => requests.find("POST", /^\/api\/filesystem\/import\/preview$/).length).toBe(1);
      requests.stop();
      const sent = requests.find("POST", /^\/api\/filesystem\/import\/preview$/)[0].body as Record<string, unknown>;
      expect(JSON.stringify(sent)).toContain("Real.Import.Film.2021.1080p.WEB-DL.mkv");
      await expectNoErrorScreen(page);
      // Nothing was written: Preview is read-only, and no import was queued.
      expect(requests.find("POST", /^\/api\/filesystem\/import\/(jobs|execute)$/)).toHaveLength(0);

      await drawer.getByRole("radio", { name: "Scan a folder" }).click();
      await expect(drawer.getByText("Folder", { exact: true }).first()).toBeVisible();
      await drawer.getByRole("button", { name: "Close", exact: true }).first().click();
      await expect(drawer).toHaveCount(0);
    } finally {
      fs.rmSync(folder, { recursive: true, force: true });
    }
  });
});

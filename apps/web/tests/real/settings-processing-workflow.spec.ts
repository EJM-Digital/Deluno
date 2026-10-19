import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Media management > Processing, against the real server, nothing stubbed (measure 4, issue 1291): a library made
 * through the real API is opened from its row, set to refine before import with a real processed-output folder, a
 * stall time, a failure choice and source cleanup, saved, and read back from the server. A Refine setting with no folder
 * cannot be saved.
 */

interface LibraryRow {
  id: string;
  name: string;
  importWorkflow: string;
  processorOutputPath: string | null;
  processorTimeoutMinutes: number | null;
  processorFailureMode: string | null;
  cleanupMode: string | null;
  removeEmptySourceFolders: boolean | null;
}

test.describe("Settings > Media management > Processing", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(150_000);

  let folder = "";
  let output = "";
  let libraryId = "";
  const name = `Real processing films ${Date.now()}`;

  test.beforeAll(() => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-processing-"));
    output = path.join(folder, "Refined");
    fs.mkdirSync(path.join(folder, "Films"));
    fs.mkdirSync(output);
  });

  test.afterAll(() => {
    fs.rmSync(folder, { recursive: true, force: true });
  });

  async function readLibrary(page: import("@playwright/test").Page) {
    const listed = await api<LibraryRow[]>(page, "GET", "/api/libraries");
    return listed.body.find((item) => item.id === libraryId)!;
  }

  test("a library made through the API shows its workflow in the list, and its row opens 'How to handle finished downloads'", async ({ page }) => {
    await openScreen(page, "/system");
    const created = await api<{ id: string }>(page, "POST", "/api/libraries", { name, mediaType: "movies", purpose: "collection", rootPath: path.join(folder, "Films") });
    expect(created.status, JSON.stringify(created.body)).toBeLessThan(300);
    libraryId = created.body.id;

    await openScreen(page, "/settings/processing");
    await untilQuiet(page);
    const row = page.getByRole("row").filter({ hasText: name });
    await expect(row).toBeVisible();
    await expect(row.getByText("Keep downloaded file")).toBeVisible();
    await row.click();

    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("How to handle finished downloads")).toBeVisible();
    await expect(drawer.getByText("Deluno will import the finished download")).toBeVisible();
  });

  test("Refine before import asks for the processed output folder and cannot be saved without one", async ({ page }) => {
    await openScreen(page, "/settings/processing");
    await untilQuiet(page);
    await page.getByRole("row").filter({ hasText: name }).click();
    const drawer = page.getByRole("dialog");

    await drawer.getByRole("radio", { name: /Refine before import/ }).click();

    await expect(drawer.getByText("Deluno will wait for the cleaned file")).toBeVisible();
    await expect(drawer.getByText("Processed output folder")).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Save workflow" })).toBeDisabled();
    expect((await readLibrary(page)).importWorkflow, "nothing was saved").not.toBe("refine-before-import");
  });

  test("with a folder, a stall time, a failure choice and source cleanup chosen, Save workflow saves them and the server reads them back", async ({ page }) => {
    await openScreen(page, "/settings/processing");
    await untilQuiet(page);
    await page.getByRole("row").filter({ hasText: name }).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("radio", { name: /Refine before import/ }).click();
    const emptyFolders = drawer.getByRole("switch", { name: /Remove empty source folders too/ });
    await expect(emptyFolders, "only usable when the source is removed after import").toBeDisabled();
    await expect(drawer.getByText("Choose Remove after import to turn this on.")).toBeVisible();

    await drawer.getByRole("textbox").first().fill(output);
    await drawer.getByRole("combobox", { name: /No progress for/ }).selectOption("1440");
    await drawer.getByRole("combobox", { name: /If processing stalls or fails/ }).selectOption("manual-review");
    await drawer.getByRole("radio", { name: "Remove after import" }).click();
    await expect(emptyFolders).toBeEnabled();
    await emptyFolders.click();

    const seen = recordRequests(page);
    await drawer.getByRole("button", { name: "Save workflow" }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    seen.stop();

    const put = seen.find("PUT", /\/api\/libraries\/[^/]+\/workflow$/);
    expect(put).toHaveLength(1);
    expect(put[0].body).toMatchObject({
      importWorkflow: "refine-before-import", processorOutputPath: output, processorTimeoutMinutes: 1440, processorFailureMode: "manual-review",
      cleanupMode: "remove-source-after-import", removeEmptySourceFolders: true
    });
    const stored = await readLibrary(page);
    expect(stored).toMatchObject({
      importWorkflow: "refine-before-import", processorOutputPath: output, processorTimeoutMinutes: 1440, processorFailureMode: "manual-review",
      cleanupMode: "remove-source-after-import", removeEmptySourceFolders: true
    });
  });

  test("the list shows what was saved: the processed folder, the wait, the failure choice and the cleanup", async ({ page }) => {
    await openScreen(page, "/settings/processing");
    await untilQuiet(page);
    const row = page.getByRole("row").filter({ hasText: name });
    await expect(row.getByText(output)).toBeVisible();
    await expect(row.getByText("Remove downloaded file")).toBeVisible();
    await expect(row.getByText("Also remove empty folders")).toBeVisible();
    await expect(row.getByText("Send to manual review")).toBeVisible();
    await expect(row.getByText("Ready")).toBeVisible();
  });

  test("choosing Keep source file turns the empty-folders choice off again and saves it", async ({ page }) => {
    await openScreen(page, "/settings/processing");
    await untilQuiet(page);
    await page.getByRole("row").filter({ hasText: name }).click();
    const drawer = page.getByRole("dialog");

    await drawer.getByRole("radio", { name: "Keep source file" }).click();
    await expect(drawer.getByRole("switch", { name: /Remove empty source folders too/ })).toBeDisabled();
    await drawer.getByRole("button", { name: "Save workflow" }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();

    expect(await readLibrary(page)).toMatchObject({ cleanupMode: "keep-source" });
  });
});

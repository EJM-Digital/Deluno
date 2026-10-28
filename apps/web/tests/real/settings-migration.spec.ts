import fs from "node:fs";
import { expect, test } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Migration against the real server, nothing stubbed (measure 4, issue 1329). Every test previews; none applies,
 * so nothing is written. A preview runs the same mapping code as an apply and is never stored.
 */

const stamp = Date.now();
const PAYLOAD = JSON.stringify({ rootFolders: [{ path: `/data/media/movies-${stamp}` }], qualityProfiles: [{ name: `Imported ${stamp}`, cutoff: 2, items: [{ allowed: true, quality: { id: 2, name: "WEB 1080p" } }] }] });

test.describe("Settings > Migration", () => {
  test.setTimeout(60_000);

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/migration");
    await untilQuiet(page);
  });

  test("choosing a source renames the import to that app, the label can be typed over, and Preview import waits for some JSON", async ({ page }) => {
    const label = page.getByLabel("Call this import");
    const preview = page.getByRole("button", { name: "Preview import" });
    await expect(page.getByLabel("Exported from")).toHaveValue("radarr");
    await expect(label).toHaveValue("Radarr");
    expect(await page.getByLabel("Exported from").locator("option").allInnerTexts()).toEqual(["Radarr", "Sonarr", "Prowlarr", "Recyclarr", "Compatible JSON"]);
    for (const [value, name] of [["sonarr", "Sonarr"], ["prowlarr", "Prowlarr"], ["recyclarr", "Recyclarr"], ["custom", "Compatible JSON"]]) {
      await page.getByLabel("Exported from").selectOption(value);
      await expect(label).toHaveValue(name);
    }
    await label.fill(`Home stack ${stamp}`);
    await expect(preview).toBeDisabled();
    await page.getByLabel("Exported JSON").fill("   ");
    await expect(preview).toBeDisabled();
    await page.getByLabel("Exported JSON").fill(PAYLOAD);
    await expect(preview).toBeEnabled();
    await page.getByRole("button", { name: "Load example" }).click();
    await expect(page.getByLabel("Exported JSON")).toHaveValue(/"Imported 1080p"/);
  });

  test("Preview sends the source, the typed label and the legacy-rules switch, says it is ready, and shows the summary strip and the change report", async ({ page }) => {
    await page.getByLabel("Exported from").selectOption("custom");
    await page.getByLabel("Call this import").fill(`Home stack ${stamp}`);
    await page.getByLabel("Exported JSON").fill(PAYLOAD);
    await page.getByRole("switch", { name: "Keep opaque rules as Advanced legacy input" }).click();
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Preview import" }).click();
    await expect(page.getByText("Preview ready. Review every create, skip and warning before applying.")).toBeVisible();
    requests.stop();

    const posts = requests.find("POST", /^\/api\/migration\/preview$/);
    expect(posts, JSON.stringify(requests.seen)).toHaveLength(1);
    expect(posts[0].body).toMatchObject({ sourceKind: "custom", sourceName: `Home stack ${stamp}`, payloadJson: PAYLOAD, allowAdvancedLegacyRules: true });
    for (const label of ["To create", "Already here", "Unsupported", "Titles seen", "Selected"]) await expect(page.getByText(label, { exact: true }).first()).toBeVisible();
    await expect(page.getByRole("heading", { name: "Change Report" })).toBeVisible();
    await expect(page.getByText(`Home stack ${stamp} · custom`)).toBeVisible();
    await expect(page.locator('#main-content [role="row"]').filter({ hasText: `Imported ${stamp}` }).first()).toBeVisible();
    // A preview is never stored: the history gains nothing.
    const stored = await api<unknown[]>(page, "GET", "/api/migration/reports");
    expect(JSON.stringify(stored.body)).not.toContain(`Home stack ${stamp}`);
  });

  test("a payload with nothing Deluno recognises says Nothing to import, and one that is not JSON says the preview found issues", async ({ page }) => {
    await page.getByLabel("Exported JSON").fill("{}");
    await page.getByRole("button", { name: "Preview import" }).click();
    await expect(page.getByText("Nothing to import", { exact: true })).toBeVisible();
    await expect(page.getByText("No configuration Deluno recognises was found in this payload. Check that you exported the whole snapshot.")).toBeVisible();

    await page.getByLabel("Exported JSON").fill("this is not json");
    await page.getByRole("button", { name: "Preview import" }).click();
    await expect(page.getByText("Preview found issues — see the change report.")).toBeVisible();
  });

  test("the safety notes open and close, and Download report saves deluno-migration-<name>.json holding the report", async ({ page }) => {
    const toggle = page.getByRole("button", { name: /What migration will and will not do/ });
    await expect(page.getByText("Existing libraries, profiles, sources and clients are skipped, never overwritten.")).toBeHidden();
    await toggle.click();
    await expect(page.getByText("A preview and an apply run the same mapping code, so what you see is what runs.")).toBeVisible();
    await expect(page.getByText("Monitored and wanted titles are reported for reconciliation, not imported blind.")).toBeVisible();
    await toggle.click();
    await expect(page.getByText("Existing libraries, profiles, sources and clients are skipped, never overwritten.")).toBeHidden();

    await page.getByLabel("Call this import").fill(`Home Stack ${stamp}!`);
    await page.getByLabel("Exported JSON").fill(PAYLOAD);
    await page.getByRole("button", { name: "Preview import" }).click();
    await expect(page.getByRole("heading", { name: "Change Report" })).toBeVisible();
    const download = page.waitForEvent("download");
    await page.getByRole("button", { name: "Download report" }).click();
    const file = await download;
    expect(file.suggestedFilename()).toBe(`deluno-migration-home-stack-${stamp}.json`);
    const saved = JSON.parse(fs.readFileSync((await file.path())!, "utf8"));
    expect(saved).toMatchObject({ sourceName: `Home Stack ${stamp}!` });
    expect(JSON.stringify(saved)).toContain(`Imported ${stamp}`);
  });
});

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Subtitles against the real server, nothing stubbed (measure 4, issue 1270): the global switches save to the
 * settings the server keeps, and a library's drawer saves its languages and rules to that library, which the server then
 * reads back. The library is this spec's own.
 */

const stamp = `sl${Date.now()}`;
let folder = "";
let libraryId = "";
const libraryName = `Subs ${stamp}`;

test.describe("Subtitles", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-subs-"));
    const page = await browser.newPage();
    await openScreen(page, "/settings/subtitles");
    const made = await api<{ id: string }>(page, "POST", "/api/libraries", { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    libraryId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/settings/subtitles");
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(folder, { recursive: true, force: true });
  });

  interface SubtitleSettings {
    subtitleLanguages: string[]; subtitleUnknownLanguage?: string; subtitleOmitLanguageCode?: boolean;
    subtitleNamePolicy?: { mustContain: string[]; mustNotContain: string[] };
    subtitleTimingPolicy?: { maxOffsetSeconds: number; syncOnlyBelow: string };
    subtitleLanguageTypePreferences?: Array<{ language: string; hearingImpaired: string; includeForced: boolean }>;
  }
  const saved = async (page: import("@playwright/test").Page) => (await api<Array<Record<string, unknown>>>(page, "GET", "/api/libraries")).body.find((l) => l.id === libraryId) as unknown as SubtitleSettings;
  // A menu inside a drawer is drawn above it but hit-tests under it: the option is chosen with a click event.
  const pick = async (page: import("@playwright/test").Page, name: string | RegExp, option: string | RegExp) => {
    await page.getByRole("dialog", { name: libraryName }).getByRole("combobox", { name }).click();
    await page.getByRole("listbox").last().getByRole("option", { name: option }).first().dispatchEvent("click");
  };

  test("a library's drawer saves its language and rules, the server reads them back, and the drawer shows them again", async ({ page }) => {
    await openScreen(page, "/settings/subtitles");
    await untilQuiet(page);
    await page.getByRole("row").filter({ hasText: libraryName }).first().click();
    const drawer = page.getByRole("dialog", { name: libraryName });
    await expect(drawer.getByText("No subtitles wanted here.")).toBeVisible();
    await drawer.getByRole("combobox", { name: "Add a subtitle language" }).selectOption("Spanish");
    await expect(drawer.getByText("Every file on this shelf gets Spanish subtitles.")).toBeVisible();
    await pick(page, "Hearing-impaired subtitles for Spanish", /^Hearing-impaired preferred/);
    await pick(page, "Unknown subtitle language", /^Spanish/);
    await pick(page, "Timing threshold", /^Same source/);
    await drawer.getByRole("switch", { name: "Name subtitles after the video only" }).click();
    await drawer.getByRole("switch", { name: "Also fetch forced Spanish subtitles" }).click();
    await drawer.getByRole("spinbutton", { name: "Maximum offset" }).fill("90");
    await drawer.getByRole("textbox", { name: "Only take releases naming" }).fill("NTb, FLUX");
    await drawer.getByRole("textbox", { name: "Never take releases naming" }).fill("HDTV, CAM");

    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    await expect.poll(() => requests.find("PUT", new RegExp(`^/api/libraries/${libraryId}/subtitles$`)).length).toBe(1);
    requests.stop();

    const stored = await saved(page);
    expect(stored.subtitleLanguages).toEqual(["es"]);
    expect(stored.subtitleOmitLanguageCode).toBe(true);
    expect(stored.subtitleNamePolicy?.mustContain.sort()).toEqual(["flux", "ntb"]);
    expect(stored.subtitleNamePolicy?.mustNotContain.sort()).toEqual(["cam", "hdtv"]);
    expect(stored.subtitleTimingPolicy).toMatchObject({ maxOffsetSeconds: 90, syncOnlyBelow: "same-source" });
    expect(stored.subtitleLanguageTypePreferences?.[0]).toMatchObject({ language: "es", includeForced: true, hearingImpaired: "hi-preferred" });
    expect(stored.subtitleUnknownLanguage).toBe("es");

    await page.reload();
    await untilQuiet(page);
    await expect(page.getByRole("row").filter({ hasText: libraryName }).first()).toContainText("Spanish");
    await page.getByRole("row").filter({ hasText: libraryName }).first().click();
    await expect(drawer.getByRole("spinbutton", { name: "Maximum offset" })).toHaveValue("90");
    await expect(drawer.getByRole("textbox", { name: "Only take releases naming" })).toHaveValue("flux, ntb");
    await expect(drawer.getByRole("switch", { name: "Name subtitles after the video only" })).toBeChecked();
  });

  test("Stop wanting a language takes it out of the library's list on Save", async ({ page }) => {
    await openScreen(page, "/settings/subtitles");
    await untilQuiet(page);
    await page.getByRole("row").filter({ hasText: libraryName }).first().click();
    const drawer = page.getByRole("dialog", { name: libraryName });
    await drawer.getByRole("button", { name: "Stop wanting Spanish subtitles", exact: true }).click();
    await expect(drawer.getByText("No subtitles wanted here.")).toBeVisible();
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    await expect.poll(async () => (await saved(page)).subtitleLanguages).toEqual([]);
  });

  test("Fetch subtitles automatically and Set permissions on written subtitles are saved to the settings the server keeps", async ({ page }) => {
    await openScreen(page, "/settings/subtitles");
    await untilQuiet(page);
    const fetchSwitch = page.getByRole("switch", { name: "Fetch subtitles automatically" });
    const was = await fetchSwitch.isChecked();
    const requests = recordRequests(page);
    try {
      await fetchSwitch.click();
      await expect(fetchSwitch).toBeChecked({ checked: !was });
      await expect.poll(() => requests.seen.filter((r) => r.method !== "GET" && r.path.startsWith("/api/settings")).length).toBeGreaterThan(0);
    } finally {
      if ((await fetchSwitch.isChecked()) !== was) await fetchSwitch.click();
      await expect(fetchSwitch).toBeChecked({ checked: was });
    }
    requests.stop();
    const permissions = page.getByRole("switch", { name: /^Set permissions on written subtitles/ });
    const permWas = await permissions.isChecked();
    try {
      await permissions.click();
      await expect(permissions).toBeChecked({ checked: !permWas });
    } finally {
      if ((await permissions.isChecked()) !== permWas) await permissions.click();
    }
  });

  test("the drawer's cleanups, subtitle colour and excluded providers are saved to the library and read back", async ({ page }) => {
    await openScreen(page, "/settings/subtitles");
    await untilQuiet(page);
    await page.getByRole("row").filter({ hasText: libraryName }).first().click();
    const drawer = page.getByRole("dialog", { name: libraryName });
    await drawer.getByRole("combobox", { name: "Add a subtitle language" }).selectOption("French");
    const cleanups = ["Remove hearing-impaired annotations", "Remove style tags", "Remove emoji", "Normalize whitespace", "Fix all-uppercase text", "Fix OCR mistakes", "Move right-to-left punctuation"];
    const before: boolean[] = [];
    for (const name of cleanups) {
      const toggle = drawer.getByRole("switch", { name });
      before.push(await toggle.isChecked());
      await toggle.click();
    }
    await drawer.getByRole("textbox", { name: "Subtitle colour" }).fill("#ffd400");
    await drawer.getByRole("textbox", { name: "Exclude providers from timing repair" }).fill("opensubtitles, subdl");
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    await expect.poll(async () => JSON.stringify(await saved(page))).toContain("ffd400");
    await page.reload();
    await untilQuiet(page);
    await page.getByRole("row").filter({ hasText: libraryName }).first().click();
    for (const [i, name] of cleanups.entries()) await expect(drawer.getByRole("switch", { name })).toBeChecked({ checked: !before[i] });
    await expect(drawer.getByRole("textbox", { name: "Subtitle colour" })).toHaveValue("#ffd400");
    await expect(drawer.getByRole("textbox", { name: "Exclude providers from timing repair" })).toHaveValue("opensubtitles, subdl");
  });

  test("Chmod and Chown group are saved when the field is left, and put back afterwards", async ({ page }) => {
    await openScreen(page, "/settings/subtitles");
    await untilQuiet(page);
    const permissions = page.getByRole("switch", { name: /^Set permissions on written subtitles/ });
    const permWas = await permissions.isChecked();
    if (!permWas) await permissions.click();
    const chmod = page.getByRole("textbox", { name: "Chmod" });
    const group = page.getByRole("textbox", { name: "Chown group" });
    const chmodWas = await chmod.inputValue();
    const groupWas = await group.inputValue();
    try {
      const requests = recordRequests(page);
      const writes = () => requests.seen.filter((r) => r.method !== "GET" && r.path.startsWith("/api/settings")).length;
      await chmod.fill("0644");
      await chmod.blur();
      await expect.poll(writes).toBe(1);
      await expect(chmod).toBeEnabled();
      await group.fill("media");
      await group.blur();
      await expect.poll(writes).toBe(2);
      requests.stop();
      await page.reload();
      await untilQuiet(page);
      await expect(page.getByRole("textbox", { name: "Chmod" })).toHaveValue("0644");
      await expect(page.getByRole("textbox", { name: "Chown group" })).toHaveValue("media");
    } finally {
      await chmod.fill(chmodWas);
      await group.fill(groupWas);
      await group.blur();
      if (!permWas) await permissions.click();
    }
  });
});

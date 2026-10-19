import { expect, test } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Release Preferences > TRaSH guide updates, against the real server, nothing stubbed (measure 4, issue 1291): the weekly
 * switch is off on a new install and its state is the server's; "Check TRaSH now" runs the real comparison and says how it went in
 * words, and changes no rule, plan or guide package by itself.
 */

interface UpdateCheck {
  isEnabled: boolean;
  status: string;
  lastCheckedUtc: string | null;
}

const STATUS_WORDS: Record<string, string> = {
  disabled: "Guide checks are off",
  "never-checked": "Ready for the first guide check",
  "up-to-date": "No tracked guide changes",
  "update-available": "Guide changes need review",
  failed: "Guide check could not finish"
};

test.describe("TRaSH guide updates", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(240_000);
  test.slow(false);

  const readCheck = (page: import("@playwright/test").Page) => api<UpdateCheck>(page, "GET", "/api/v1/guides/trash/update-check");

  test("the weekly check is off on a new install and the page says so", async ({ page }) => {
    await openScreen(page, "/settings/custom-formats");
    await untilQuiet(page);
    expect((await readCheck(page)).body.isEnabled).toBe(false);

    const weekly = page.getByRole("switch", { name: /Check upstream guide changes weekly/ });
    await expect(weekly).toHaveAttribute("aria-checked", "false");
    await expect(page.getByText("Guide checks are off")).toBeVisible();
    await expect(page.getByText("Enable checks to allow an outbound TRaSH Guides comparison.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Check TRaSH now" })).toBeDisabled();
  });

  test("turning the weekly check on says so, is kept by the server, and offers the first check; turning it off says so too", async ({ page }) => {
    await openScreen(page, "/settings/custom-formats");
    await untilQuiet(page);
    const weekly = page.getByRole("switch", { name: /Check upstream guide changes weekly/ });

    await weekly.click();

    await expect(page.getByText("Weekly guide-change checks enabled")).toBeVisible();
    await expect(weekly).toHaveAttribute("aria-checked", "true");
    await expect(page.getByText(STATUS_WORDS["never-checked"])).toBeVisible();
    await expect(page.getByRole("button", { name: "Check TRaSH now" })).toBeEnabled();
    expect((await readCheck(page)).body).toMatchObject({ isEnabled: true, status: "never-checked" });

    await page.reload();
    await untilQuiet(page);
    await expect(page.getByRole("switch", { name: /Check upstream guide changes weekly/ })).toHaveAttribute("aria-checked", "true");

    await page.getByRole("switch", { name: /Check upstream guide changes weekly/ }).click();
    await expect(page.getByText("Guide-change checks disabled")).toBeVisible();
    expect((await readCheck(page)).body.isEnabled).toBe(false);
    await expect(page.getByText(STATUS_WORDS.disabled)).toBeVisible();
  });

  test("Check TRaSH now runs the comparison, says how it went in words, records when it ran, and changes no guide package by itself", async ({ page }) => {
    await openScreen(page, "/settings/custom-formats");
    await untilQuiet(page);
    const packageBefore = await api<{ integritySha256: string }>(page, "GET", "/api/v1/guides/trash/package");
    expect(packageBefore.status).toBe(200);
    await page.getByRole("switch", { name: /Check upstream guide changes weekly/ }).click();
    await expect(page.getByText("Weekly guide-change checks enabled")).toBeVisible();

    await page.getByRole("button", { name: "Check TRaSH now" }).click();

    const finished = Object.values(STATUS_WORDS).filter((words) => words !== STATUS_WORDS.disabled && words !== STATUS_WORDS["never-checked"]);
    await expect.poll(async () => {
      for (const words of finished) if (await page.getByText(words, { exact: true }).isVisible()) return words;
      return "";
    }, { timeout: 200_000, intervals: [1000, 2000, 5000] }).not.toBe("");
    const stored = (await readCheck(page)).body;
    expect(["up-to-date", "update-available", "failed"]).toContain(stored.status);
    expect(stored.lastCheckedUtc, "the check records when it ran").not.toBeNull();
    await expect(page.getByText(STATUS_WORDS[stored.status], { exact: true })).toBeVisible();

    const packageAfter = await api<{ integritySha256: string }>(page, "GET", "/api/v1/guides/trash/package");
    expect(packageAfter.body.integritySha256, "a check never changes the guide package").toBe(packageBefore.body.integritySha256);

    await page.getByRole("switch", { name: /Check upstream guide changes weekly/ }).click();
    await expect(page.getByText("Guide-change checks disabled")).toBeVisible();
  });

  async function applyNextVersion(page: import("@playwright/test").Page) {
    const stored = (await api<Array<{ package: { version: number }; integritySha256: string; isActive: boolean }>>(page, "GET", "/api/v1/guides/trash/versions")).body;
    const active = stored.find((item) => item.isActive)!;
    const guidePackage = (await api<{ version: number }>(page, "GET", "/api/v1/guides/trash/package")).body;
    const applied = await api(page, "POST", "/api/v1/guides/trash/apply", {
      package: { ...guidePackage, version: Math.max(guidePackage.version, ...stored.map((item) => item.package.version)) + 1 },
      expectedCurrentIntegritySha256: active.integritySha256
    });
    expect(applied.status, JSON.stringify(applied.body).slice(0, 300)).toBe(201);
  }

  test("version history lists every retained version, goes back to an older one, and keeps them all", async ({ page }) => {
    await openScreen(page, "/settings/custom-formats");
    // The version Deluno shipped stays in the history (next test), so one applied version leaves two.
    await applyNextVersion(page);
    const retained = (await api<Array<{ package: { version: number }; isActive: boolean }>>(page, "GET", "/api/v1/guides/trash/versions")).body;
    const versions = retained.map((item) => item.package.version).sort((a, b) => a - b);
    expect(versions).toHaveLength(2);
    const [older, newer] = versions;

    await page.reload();
    await untilQuiet(page);
    await page.getByText("Guide version history").click();
    const history = page.getByLabel("Guide package versions");
    await expect(history.getByText(`Version ${newer} · in use`)).toBeVisible();
    await expect(history.getByText(`Version ${older}`, { exact: true })).toBeVisible();

    await history.getByRole("button", { name: "Go back to this version" }).click();

    await expect(page.getByText(`Guide package version ${older} is in use again`)).toBeVisible();
    await expect(history.getByText(`Version ${older} · in use`)).toBeVisible();
    const after = (await api<Array<{ package: { version: number }; isActive: boolean }>>(page, "GET", "/api/v1/guides/trash/versions")).body;
    expect(after.map((item) => item.package.version).sort((a, b) => a - b)).toEqual([older, newer]);
    expect(after.filter((item) => item.isActive).map((item) => item.package.version)).toEqual([older]);
  });

  test("the guide package Deluno shipped stays in the history after the first sync, so it can be gone back to (after one applied version the server lists only that version)", async ({ page }) => {
    await openScreen(page, "/settings/custom-formats");
    const shippedVersion = (await api<{ version: number }>(page, "GET", "/api/v1/guides/trash/package")).body.version;
    await applyNextVersion(page);
    const versions = (await api<Array<{ package: { version: number } }>>(page, "GET", "/api/v1/guides/trash/versions")).body.map((item) => item.package.version);
    expect(versions).toContain(shippedVersion);
  });
});

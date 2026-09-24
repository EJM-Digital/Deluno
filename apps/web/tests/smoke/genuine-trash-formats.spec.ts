import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * The guide catalogue is TRaSH's own formats now, per media type, and rules
 * made from the catalogue Deluno shipped before are moved onto them from a
 * preview. Both walked through the page the way an owner would.
 */

const TRASH_AMZN_MOVIES = "b3b3a6ac74ecbd56bcdbefa4799fb9df";
const TRASH_AMZN_TV = "d660701077794679fd59e8bdf4ce3a29";
const OLD_CATALOGUE_AMAZON = "4e9a630db98d5391aec1368a0256e2fe";

interface RuleRow {
  id: string;
  name: string;
  mediaType: string;
  trashId?: string | null;
  conditions: string;
  score: number;
}

async function authHeaders(page: import("@playwright/test").Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

test.describe("genuine TRaSH formats", () => {
  test("a TV rule chosen from the guide is TRaSH's TV format, with TRaSH's conditions", async ({ page }) => {
    const name = `Smoke AMZN TV ${Date.now()}`;
    await authenticateAndNavigate(page, "/settings/custom-formats");
    const headers = await authHeaders(page);
    await page.getByRole("button", { name: "New custom rule" }).first().click();

    const drawer = page.getByRole("dialog", { name: "New custom release rule" });
    await drawer.getByRole("radio", { name: "TV" }).click();
    const guideChoice = drawer.getByLabel("Guide choice");
    // TRaSH's movies AMZN is a different format; a TV rule is never offered it.
    await expect(guideChoice.locator(`option[value='${TRASH_AMZN_MOVIES}']`)).toHaveCount(0);
    await guideChoice.selectOption(TRASH_AMZN_TV);

    const conditions = drawer.getByLabel("Matching conditions");
    await expect(conditions).toContainText("Release title");
    await expect(conditions).toContainText("WEBDL");
    await expect(conditions).toContainText("WEBRIP");
    await expect(drawer.getByRole("button", { name: "Add matching criterion" })).toHaveCount(0);

    await drawer.getByLabel("Name", { exact: true }).fill(name);
    await drawer.getByRole("button", { name: "Create rule" }).click();

    // The drawer stays on the new rule and shows what the server saved.
    const saved = page.getByRole("dialog");
    await expect(saved.getByText("Saved just now")).toBeVisible();
    await expect(saved.getByLabel("Matching conditions")).toContainText("WEBDL");

    const rules = await (await page.request.get("/api/custom-formats", { headers })).json() as RuleRow[];
    const created = rules.find((rule) => rule.name === name);
    try {
      expect(created?.mediaType).toBe("tv");
      expect(created?.trashId).toBe(TRASH_AMZN_TV);
      expect(created?.conditions).toContain("\"implementation\":\"SourceSpecification\"");

      // Saving it again, and opening it from the list and saving, both leave
      // TRaSH's specifications whole.
      await saved.getByRole("radio", { name: /Strongly prefer/ }).click();
      await saved.getByRole("button", { name: "Save rule" }).click();
      await expect(saved.getByText("Saved just now")).toBeVisible();
      await page.keyboard.press("Escape");
      await page.reload();
      await page.getByRole("row").filter({ hasText: name }).click();
      const editDrawer = page.getByRole("dialog");
      await expect(editDrawer.getByLabel("Matching conditions")).toContainText("WEBDL");
      await editDrawer.getByRole("radio", { name: /^Prefer/ }).click();
      await editDrawer.getByRole("button", { name: "Save rule" }).click();
      await expect(editDrawer.getByText("Saved just now")).toBeVisible();
      const after = (await (await page.request.get("/api/custom-formats", { headers })).json() as RuleRow[]).find((rule) => rule.id === created!.id);
      expect(after?.conditions).toBe(created!.conditions);
      expect(after?.score).toBe(100);
    } finally {
      if (created) await page.request.delete(`/api/custom-formats/${created.id}`, { headers });
    }
  });

  test("a rule from the old catalogue moves onto TRaSH's format and keeps its profile answer", async ({ page }) => {
    const stamp = Date.now();
    await authenticateAndNavigate(page, "/settings/custom-formats");
    const headers = await authHeaders(page);

    const createRule = await page.request.post("/api/custom-formats", {
      headers,
      data: {
        name: `Amazon Prime ${stamp}`,
        mediaType: "movies",
        score: 100,
        trashId: OLD_CATALOGUE_AMAZON,
        conditions: JSON.stringify(["\\bAMZN\\b", "\\bAmazon\\b"].map((value) => ({ type: "releaseTitle", value, negate: false, required: true }))),
        upgradeAllowed: true
      }
    });
    expect(createRule.ok()).toBe(true);
    const rule = await createRule.json() as RuleRow;
    const createProfile = await page.request.post("/api/quality-profiles", {
      headers,
      data: {
        name: `Smoke move profile ${stamp}`,
        mediaType: "movies",
        cutoffQuality: "WEB 1080p",
        allowedQualities: "WEB 1080p",
        customFormatIds: rule.id,
        upgradeUntilCutoff: true,
        upgradeUnknownItems: false,
        formatIntents: { [rule.id]: "prefer" }
      }
    });
    expect(createProfile.ok()).toBe(true);
    const profile = await createProfile.json() as { id: string };

    try {
      await page.reload();
      await expect(page.getByText("Rules from the old guide catalogue")).toBeVisible();
      await page.getByRole("button", { name: "Review the move" }).click();

      const drawer = page.getByRole("dialog", { name: "Move rules onto TRaSH's formats" });
      const ours = drawer.getByRole("checkbox", { name: `Move Amazon Prime ${stamp} (Movies)` });
      await expect(ours).toBeChecked();
      await expect(drawer).toContainText("Becomes TRaSH's “AMZN” for movies");
      await expect(drawer).toContainText(`Used by Smoke move profile ${stamp}`);
      await drawer.getByRole("button", { name: /^Move \d+ rules?$/ }).click();
      await expect(drawer).toContainText("each move is recorded in Activity");

      const moved = (await (await page.request.get("/api/custom-formats", { headers })).json() as RuleRow[]).find((item) => item.id === rule.id);
      expect(moved?.trashId).toBe(TRASH_AMZN_MOVIES);
      expect(moved?.conditions).toContain("\"implementation\":\"SourceSpecification\"");

      const profileAfter = (await (await page.request.get("/api/quality-profiles", { headers })).json() as Array<{ id: string; customFormatIds: string; formatIntents?: Record<string, string> }>)
        .find((item) => item.id === profile.id);
      expect(profileAfter?.customFormatIds).toContain(rule.id);
      expect(profileAfter?.formatIntents?.[rule.id]).toBe("prefer");

      const activity = await (await page.request.get(`/api/activity?relatedEntityType=custom-format&relatedEntityId=${rule.id}`, { headers })).json() as { items: Array<{ category: string; message: string }> };
      expect(activity.items.some((item) => item.category === "quality.custom-format.guide-moved" && item.message.includes("AMZN"))).toBe(true);
    } finally {
      await page.request.delete(`/api/quality-profiles/${profile.id}`, { headers });
      await page.request.delete(`/api/custom-formats/${rule.id}`, { headers });
    }
  });
});

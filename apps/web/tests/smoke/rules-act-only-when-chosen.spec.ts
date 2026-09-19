import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #550, decided by the owner on 2026-09-19. Before the soak the rig's TV profile held
// every episode for review because a rule nobody had answered took the guide's
// score (1650) as "Strongly prefer" - and the profile screen showed that guess
// highlighted as though somebody had chosen it. A rule is now off until a person
// turns it on; the guide's view is a suggestion one click adopts.
const authHeaders = (token: string | null) => ({ Authorization: `Bearer ${token}`, "Content-Type": "application/json" });

test.describe("a profile's rules act only when chosen", () => {
  test("a rule starts off beside the guide's suggestion, and Duplicate opens a copy to change", async ({ page }) => {
    const stamp = Date.now();
    const ruleName = `Smoke Trusted Group ${stamp}`;
    const profileName = `Smoke-Rules-Off-${stamp}`;
    await authenticateAndNavigate(page, "/settings/profiles");
    const headers = authHeaders(await page.evaluate(() => sessionStorage.getItem("deluno-auth-token")));

    const tier = ((await (await page.request.get("/api/quality-model", { headers })).json()) as { tiers: Array<{ name: string }> }).tiers[0]?.name;
    const rule = await page.request.post("/api/custom-formats", {
      headers,
      data: {
        name: ruleName,
        mediaType: "movies",
        score: 1650,
        trashId: null,
        conditions: JSON.stringify([{ type: "releaseGroup", value: "TRUSTED", negate: false, required: true }]),
        upgradeAllowed: true
      }
    });
    expect(rule.ok()).toBe(true);
    const ruleId = ((await rule.json()) as { id: string }).id;
    // In the profile, and nobody has said what they want from it.
    const create = await page.request.post("/api/quality-profiles", {
      headers,
      data: { name: profileName, mediaType: "movies", cutoffQuality: tier, allowedQualities: tier, customFormatIds: ruleId, upgradeUntilCutoff: true, upgradeUnknownItems: false }
    });
    expect(create.ok()).toBe(true);
    const profileId = ((await create.json()) as { id: string }).id;

    try {
      await page.reload();
      await page.getByRole("row").filter({ hasText: profileName }).click();
      const drawer = page.getByRole("dialog");
      await drawer.getByRole("button", { name: /How should your own rules affect a choice/ }).click();

      const answers = drawer.getByRole("radiogroup", { name: `How much this profile wants ${ruleName}` });
      await expect(answers.getByRole("radio", { name: "Off", exact: true })).toBeChecked();
      await expect(answers.getByRole("radio", { name: "Strongly prefer", exact: true })).not.toBeChecked();

      // One click adopts what the guide would have chosen, and it is then theirs.
      await drawer.getByRole("button", { name: `Use the guide's suggestion for ${ruleName}: Strongly prefer` }).click();
      await expect(answers.getByRole("radio", { name: "Strongly prefer", exact: true })).toBeChecked();
      await drawer.getByRole("button", { name: "Save quality profile", exact: true }).click();
      await expect.poll(async () => {
        const profiles = (await (await page.request.get("/api/quality-profiles/", { headers })).json()) as Array<{ id: string; formatIntents?: Record<string, string> }>;
        return profiles.find((item) => item.id === profileId)?.formatIntents?.[ruleId];
      }).toBe("strong-prefer");

      // A copy to change, with the same rules and answers. Nothing exists until it is saved.
      await drawer.getByRole("button", { name: "Duplicate", exact: true }).click();
      await expect(drawer.getByLabel("Profile name", { exact: true })).toHaveValue(`Copy of ${profileName}`);
      const ownRules = drawer.getByRole("button", { name: /How should your own rules affect a choice/ });
      if ((await ownRules.getAttribute("aria-expanded")) !== "true") await ownRules.click();
      await expect(drawer.getByRole("radiogroup", { name: `How much this profile wants ${ruleName}` }).getByRole("radio", { name: "Strongly prefer", exact: true })).toBeChecked();
    } finally {
      const profiles = (await (await page.request.get("/api/quality-profiles/", { headers })).json()) as Array<{ id: string; name: string }>;
      for (const item of profiles.filter((candidate) => candidate.name.includes(profileName))) await page.request.delete(`/api/quality-profiles/${item.id}`, { headers });
      await page.request.delete(`/api/custom-formats/${ruleId}`, { headers });
    }
  });
});

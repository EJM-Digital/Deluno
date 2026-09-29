import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1055. On the rig, at 1920px, the processor row's Test, Edit, Path mappings and
// Remove buttons sat on top of each other and a real click on Edit landed on Path
// mappings. They now have a track of their own on wide screens and wrap onto a line
// under the name on a phone.
const weir = {
  id: "processor-weir",
  name: "Weir",
  provider: "generic-webhook",
  submissionUrl: "http://127.0.0.1:9347/api/v1/intake/webhook/deluno",
  healthCheckUrl: null,
  authHeaderName: "X-Webhook-Secret",
  secretConfigured: true,
  isEnabled: true,
  healthStatus: "degraded",
  lastHealthMessage: "Deluno could not confirm it is working.",
  lastHealthTestUtc: "2026-09-29T04:00:00Z",
  createdUtc: "2026-09-29T00:00:00Z",
  updatedUtc: "2026-09-29T04:00:00Z"
};

const widths = [
  { width: 390, height: 844 },
  { width: 1669, height: 1000 },
  { width: 1920, height: 1080 }
];

for (const size of widths) {
  test(`processor row buttons do not overlap and Edit opens its drawer at ${size.width}px`, async ({ page }) => {
    await page.setViewportSize(size);
    await page.route((url) => url.pathname === "/api/integrations/processors/connections", async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ contentType: "application/json", body: JSON.stringify([weir]) });
        return;
      }
      await route.continue();
    });
    await authenticateAndNavigate(page, "/settings/processing");

    const row = page.getByRole("row").filter({ hasText: "Weir" }).first();
    await expect(row).toBeVisible();
    const buttons = [
      row.getByRole("button", { name: "Test", exact: true }),
      row.getByRole("button", { name: "Edit Weir", exact: true }),
      row.getByRole("button", { name: "Path mappings", exact: true }),
      row.getByRole("button", { name: "Remove", exact: true })
    ];
    const boxes = [];
    for (const button of buttons) {
      await expect(button).toBeVisible();
      const box = await button.boundingBox();
      expect(box).not.toBeNull();
      boxes.push(box!);
    }
    for (let first = 0; first < boxes.length; first++) {
      for (let second = first + 1; second < boxes.length; second++) {
        const a = boxes[first];
        const b = boxes[second];
        const overlaps = a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
        expect(overlaps, `buttons ${first} and ${second} overlap at ${size.width}px`).toBe(false);
      }
    }

    // A real click, so anything on top of Edit would take it.
    await buttons[1].click();
    const drawer = page.getByRole("dialog", { name: "Edit processor connection" });
    await expect(drawer).toBeVisible();
    await expect(drawer.getByLabel("Health check address")).toBeVisible();
  });
}

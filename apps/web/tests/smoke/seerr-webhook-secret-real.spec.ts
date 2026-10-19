import { expect as baseExpect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

const expect = baseExpect.configure({ timeout: 20_000 });

// The Seerr webhook secret against the real host: nothing here is stubbed. A person's clicks in the drawer go to the real API and the real
// database, and the secret the screen shows is the one the real webhook route then accepts. No Seerr is needed: Seerr's own "test"
// notification is a body the route acknowledges and ignores, so a 200 proves the secret and a 401 proves the refusal.

test.skip(({ isMobile }) => isMobile, "This drawer is the same on a phone.");

async function headers(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/** What Seerr 3.5.0 sends from its own test button (tests/fixtures/recorded/seerr: webhook-test). */
const seerrTestNotification = { notification_type: "TEST_NOTIFICATION", event: "", subject: "Test Notification", message: "Check check, 1, 2, 3. Are we coming in clear?", image: "", media: null, request: null, extra: [] };

async function callWebhook(page: Page, id: string, secret: string | null) {
  const response = await page.request.post(`/api/seerr-servers/${id}/webhook`, {
    headers: secret === null ? {} : { Authorization: secret },
    data: seerrTestNotification
  });
  return response.status();
}

test("Generate shows the secret once and the real webhook takes it; Regenerate shows a new one and the old one stops working at once", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/media-servers");
  const stamp = Date.now();
  const created = await page.request.post("/api/seerr-servers", {
    headers: await headers(page),
    data: { name: `Seerr ${stamp}`, baseUrl: `http://seerr-${stamp}.example.test:5055`, apiKey: "seed-key", isEnabled: true }
  });
  expect(created.ok(), `POST /api/seerr-servers answered ${created.status()}: ${await created.text()}`).toBe(true);
  const server = (await created.json()) as { id: string; name: string };
  try {
    // No secret yet: the screen says so, and the webhook refuses everything.
    expect(await callWebhook(page, server.id, "anything")).toBe(401);
    await page.reload();
    await page.getByText(server.name, { exact: true }).first().click();
    const drawer = page.getByRole("dialog", { name: server.name });
    const field = drawer.getByLabel("Authorization header value");
    await expect(field).toHaveAttribute("placeholder", "Not generated yet");

    // Generate: the secret is shown in clear, once, and the real route takes it.
    await drawer.getByRole("button", { name: "Generate" }).click();
    await expect(drawer.getByText("Shown once. Paste it into Seerr's Authorization Header field now.")).toBeVisible();
    const first = await field.inputValue();
    expect(first.length).toBeGreaterThan(16);
    expect(await callWebhook(page, server.id, first)).toBe(200);
    expect(await callWebhook(page, server.id, `${first}x`)).toBe(401);
    expect(await callWebhook(page, server.id, null)).toBe(401);

    // Regenerate: a new secret, and the one just shown is refused from this moment.
    await drawer.getByRole("button", { name: "Regenerate" }).click();
    await expect(async () => expect(await field.inputValue()).not.toBe(first)).toPass();
    const second = await field.inputValue();
    expect(second.length).toBeGreaterThan(16);
    expect(await callWebhook(page, server.id, first)).toBe(401);
    expect(await callWebhook(page, server.id, second)).toBe(200);

    // The secret is never shown again: the list and a reopened drawer say only that one exists.
    const listed = await page.request.get("/api/seerr-servers", { headers: await headers(page) });
    const body = await listed.text();
    expect(body).not.toContain(first);
    expect(body).not.toContain(second);
    expect(JSON.parse(body).find((item: { id: string }) => item.id === server.id).hasWebhookSecret).toBe(true);
    await page.keyboard.press("Escape");
    await page.reload();
    await page.getByText(server.name, { exact: true }).first().click();
    const reopened = page.getByRole("dialog", { name: server.name }).getByLabel("Authorization header value");
    await expect(reopened).toHaveValue("••••••••••••••••");
  } finally {
    await page.request.delete(`/api/seerr-servers/${server.id}`, { headers: await headers(page) });
  }
});

import { expect as baseExpect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

const expect = baseExpect.configure({ timeout: 20_000 });

// Find & Download, Download Clients, against the real host with nothing stubbed: for each kind of client Deluno can send to, a person
// opens New client, picks the kind, fills it in and adds it; edits it and saves; switches it off and on from the list; and removes it.
// Every step is read back from the server, not from the screen. The clients are addresses nobody listens on: nothing here depends on
// another app, and what a real send to each kind does is proven by the recorded replies and the end-to-end suite.

test.skip(({ isMobile }) => isMobile, "The client drawer and the list are the same on a phone.");

type Row = Record<string, unknown> & { id: string; name: string };

const KINDS = [
  { protocol: "qbittorrent", label: "qBittorrent", port: "8080", secretLabel: "Password", usernameBlank: false, urlBase: "" },
  { protocol: "transmission", label: "Transmission", port: "9091", secretLabel: "Password", usernameBlank: false, urlBase: "" },
  { protocol: "deluge", label: "Deluge", port: "8112", secretLabel: "Password", usernameBlank: false, urlBase: "" },
  { protocol: "utorrent", label: "uTorrent", port: "8080", secretLabel: "Password", usernameBlank: false, urlBase: "" },
  { protocol: "rtorrent", label: "rTorrent", port: "8000", secretLabel: "Password", usernameBlank: false, urlBase: "/RPC2" },
  { protocol: "sabnzbd", label: "SABnzbd", port: "8080", secretLabel: "API key", usernameBlank: true, urlBase: "sabnzbd" },
  { protocol: "nzbget", label: "NZBGet", port: "6789", secretLabel: "Password", usernameBlank: false, urlBase: "" }
] as const;

async function headers(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function clients(page: Page): Promise<Row[]> {
  const response = await page.request.get("/api/download-clients", { headers: await headers(page) });
  expect(response.ok(), `GET /api/download-clients answered ${response.status()}`).toBe(true);
  return await response.json() as Row[];
}

for (const kind of KINDS) {
  test(`${kind.label}: added in the New client drawer, edited, switched off and on from the list, and removed`, async ({ page }) => {
    test.setTimeout(120_000);
    page.setDefaultTimeout(15_000);
    await authenticateAndNavigate(page, "/indexers/download-clients");
    const before = new Set((await clients(page)).map((item) => item.id));
    const host = `${kind.protocol}-walk-${Date.now()}.example.test`;
    let madeId: string | undefined;
    try {
      // Add
      await page.getByRole("button", { name: "New client" }).first().click();
      const drawer = page.getByRole("dialog").filter({ has: page.getByLabel("Host or IP") });
      await expect(drawer).toBeVisible();
      await drawer.getByLabel("Client", { exact: true }).selectOption(kind.protocol);
      await expect(drawer.getByLabel("Port", { exact: true })).toHaveValue(kind.port);
      if (kind.urlBase) await drawer.getByRole("button", { name: /^Advanced/ }).click();
      if (kind.urlBase === "/RPC2") await expect(drawer.getByLabel(/^URL base/)).toHaveValue("/RPC2");
      await drawer.getByLabel("Host or IP", { exact: true }).selectOption({ label: "Custom host / IP" });
      await drawer.getByPlaceholder("Hostname or IP address").fill(host);
      if (!kind.usernameBlank) await drawer.getByLabel(/^Username/).fill("deluno");
      await drawer.getByLabel(new RegExp(`^${kind.secretLabel}`)).fill("a-test-secret");
      if (kind.urlBase === "sabnzbd") await drawer.getByLabel(/^URL base/).fill("sabnzbd");
      await drawer.getByRole("button", { name: "Add client" }).click();
      await expect(page.getByText("Client added").first()).toBeVisible();

      const made = (await clients(page)).find((item) => !before.has(item.id));
      expect(made, "the new client is in the list").toBeTruthy();
      madeId = made!.id;
      expect(made).toMatchObject({ protocol: kind.protocol, host, port: Number(kind.port) });
      if (kind.urlBase) expect(made!.urlBase).toBe(kind.urlBase.replace(/^\//, ""));
      expect(JSON.stringify(made)).not.toContain("a-test-secret");

      // Edit: the drawer holds the saved client; a changed label (the account name, or the Host of a client with none) is saved and read back.
      await expect(drawer.getByRole("button", { name: /^Save client$/ })).toBeVisible();
      await drawer.getByPlaceholder("Hostname or IP address").fill(`edited-${host}`);
      await drawer.getByRole("button", { name: /^Save client$/ }).click();
      await expect.poll(async () => (await clients(page)).find((item) => item.id === madeId)?.host).toBe(`edited-${host}`);
      expect((await clients(page)).find((item) => item.id === madeId)).toMatchObject({ protocol: kind.protocol, host: `edited-${host}`, port: Number(kind.port) });

      // Switch off and on from the list
      await page.goto("/indexers/download-clients");
      const row = page.getByRole("row").filter({ hasText: host }).first();
      await expect(row).toBeVisible();
      const wasOn = (await clients(page)).find((item) => item.id === madeId)!.isEnabled as boolean;
      for (const expected of [!wasOn, wasOn]) {
        const put = page.waitForResponse((response) => response.request().method() === "PUT" && new URL(response.url()).pathname === `/api/download-clients/${madeId}`);
        await row.getByRole("switch").first().click();
        expect((await put).ok()).toBe(true);
        expect((await clients(page)).find((item) => item.id === madeId)!.isEnabled).toBe(expected);
      }

      // Remove
      await row.click();
      const saved = page.getByRole("dialog").filter({ has: page.getByLabel("Host or IP") });
      await expect(saved).toBeVisible();
      await saved.getByRole("button", { name: "Remove…" }).click();
      await page.getByRole("alertdialog").or(page.getByRole("dialog", { name: /^Remove /i })).getByRole("button", { name: "Remove client" }).click();
      await expect.poll(async () => (await clients(page)).some((item) => item.id === madeId)).toBe(false);
      madeId = undefined;
    } finally {
      if (madeId) await page.request.delete(`/api/download-clients/${madeId}`, { headers: await headers(page) }).catch(() => undefined);
    }
  });
}

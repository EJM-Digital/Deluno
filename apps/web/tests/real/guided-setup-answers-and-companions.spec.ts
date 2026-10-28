import { expect, test } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Guided setup against the real server, nothing stubbed (issue 1283): the Yes/No answers saved in the draft and
 * restored after a reload, the list of bundled apps the Connect step reads, and the Companion Tools page asking the
 * tray for an app only the tray installs. Each spec clears the draft first, so it depends on no other spec's data.
 */

interface Component { kind: string; displayName: string; category: string; installed: boolean; reachable: boolean; connected: boolean; needsAccount: boolean; accountExists: boolean }

test.describe("Guided setup", () => {
  test.describe.configure({ mode: "serial" });

  test("Yes/No answers are saved in the draft and restored after a reload, and an unanswered question stays unanswered", async ({ page }) => {
    await openScreen(page, "/setup-guide");
    await api(page, "DELETE", "/api/setup/draft");
    await page.reload();
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    await page.getByRole("button", { name: /How you sign in/ }).click();
    await page.getByRole("radio", { name: "Skip sign-in for devices on your home network? No" }).click();
    await page.getByRole("button", { name: /^(?:\d+ )?Updates\b/ }).click();
    await page.getByRole("radio", { name: "Check for updates by itself? Yes" }).click();
    // The draft is saved half a second after the last change: wait for that save, not for a guessed time.
    const saved = page.waitForResponse((response) => response.url().includes("/api/setup/draft") && response.request().method() === "PUT", { timeout: 10_000 });
    await page.getByRole("radio", { name: "Update Deluno automatically? No" }).click();
    await saved;

    const stored = await api<{ skipSignInLocalNetwork?: boolean | null; checkForUpdates?: boolean | null }>(page, "GET", "/api/setup/draft");
    expect(stored.status).toBe(200);
    expect(stored.body.skipSignInLocalNetwork).toBe(false);
    expect(stored.body.checkForUpdates).toBe(true);

    await page.reload();
    await untilQuiet(page);
    await page.getByRole("button", { name: /How you sign in/ }).click();
    await expect(page.getByRole("radio", { name: "Skip sign-in for devices on your home network? No" })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("radio", { name: "Skip sign-in for devices on your home network? Yes" })).toHaveAttribute("aria-checked", "false");
    await page.getByRole("button", { name: /^(?:\d+ )?Updates\b/ }).click();
    await expect(page.getByRole("radio", { name: "Check for updates by itself? Yes" })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("radio", { name: "Update Deluno automatically? No" })).toHaveAttribute("aria-checked", "true");
    // Never answered: neither side is ticked after the reload.
    await page.getByRole("button", { name: /^(?:\d+ )?Automation\b/ }).click();
    const firstQuestion = page.getByRole("radiogroup").first();
    await expect(firstQuestion.getByRole("radio").first()).toHaveAttribute("aria-checked", "false");
    await expect(firstQuestion.getByRole("radio").nth(1)).toHaveAttribute("aria-checked", "false");
    await api(page, "DELETE", "/api/setup/draft");
  });

  test("GET /api/system/bundled-components lists the six apps with their state and no password", async ({ page }) => {
    await openScreen(page, "/setup-guide");
    const listed = await api<Component[]>(page, "GET", "/api/system/bundled-components");
    expect(listed.status).toBe(200);
    expect(listed.body.map((app) => app.kind).sort()).toEqual(["deluge", "nzbget", "qbittorrent", "sabnzbd", "transmission", "weir"]);
    expect(listed.body.find((app) => app.kind === "weir")?.category).toBe("processor");
    for (const app of listed.body) {
      for (const field of ["installed", "reachable", "connected", "needsAccount", "accountExists"] as const) expect(typeof app[field], `${app.kind}.${field}`).toBe("boolean");
    }
    expect(JSON.stringify(listed.body)).not.toMatch(/password|secret|apiKey|token/i);
  });

  test("ticking a tray-only app and pressing Set up asks the tray alone and says so in words", async ({ page }) => {
    await openScreen(page, "/indexers/companion-tools");
    await untilQuiet(page);
    await expectNoErrorScreen(page);
    const requests = recordRequests(page);

    await page.getByRole("checkbox", { name: "Set up Transmission" }).check();
    await page.getByRole("button", { name: "Set up 1 tool", exact: true }).click();

    await expect(page.getByText(/Asked the Deluno icon in your taskbar to install Transmission\./)).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/companion-tools\/install$/)).toHaveLength(0);
    const posts = requests.find("POST", /^\/api\/companion-tools\/tray-install$/);
    expect(posts, JSON.stringify(requests.seen)).toHaveLength(1);
    expect(posts[0].body).toEqual({ toolIds: ["transmission"] });
    await expect(page.getByRole("button", { name: "Set up tools", exact: true })).toBeDisabled();
  });
});

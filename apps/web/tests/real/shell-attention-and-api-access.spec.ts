import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Two things the shell leans on, against the real host with nothing stubbed:
 *  - what the bell and the sidebar read again when the server pushes "attention changed", and
 *  - System > API Access, the one place a key for another app is made, shown once and taken away.
 */

/** Waits until this page has joined the dashboard subject, so a push raised now reaches it. */
async function joinedTheDashboard(page: Page) {
  await expect.poll(async () => (await api<{ dashboard: number }>(page, "GET", "/api/testing/realtime/watchers")).body.dashboard, { timeout: 20_000 }).toBeGreaterThan(0);
}

async function pushAttention(page: Page, reason: string) {
  const answer = await api(page, "POST", "/api/testing/realtime/event", { eventName: "AttentionChanged", subject: "dashboard", payload: { reason } });
  expect(answer.status).toBe(204);
}

/** The lists the sidebar's count is made of, by the address each is read from. */
const LISTS = {
  movieRecovery: /^\/api\/movies\/import-recovery$/,
  seriesRecovery: /^\/api\/series\/import-recovery$/,
  failedJobs: /^\/api\/jobs/,
  handoffs: /^\/api\/integrations\/processors\/handoffs$/,
  orphans: /^\/api\/download-clients\/orphans$/,
  health: /^\/api\/system\/health\/summary$/,
  summary: /^\/api\/attention\/summary$/,
  indexers: /^\/api\/indexers$/,
  movieWanted: /^\/api\/movies\/wanted$/,
  dispatches: /^\/api\/download-dispatches$/
} as const;
type ListName = keyof typeof LISTS;

/** Pushes one reason and returns which lists the page then read again. */
async function readOnce(page: Page, reason: string): Promise<ListName[]> {
  const requests = recordRequests(page);
  await pushAttention(page, reason);
  // The page folds a burst into one read (400 ms) and then reads; wait for the summary the server counts from, which every reason ends with except heldReleases.
  if (reason !== "heldReleases") await expect.poll(() => requests.find("GET", LISTS.summary).length, { timeout: 15_000 }).toBeGreaterThan(0);
  // Give any read that should NOT happen the time a wrong one would take.
  await page.waitForTimeout(1_500); // reason: proves a negative; a read the push should not cause would have started within the 400 ms fold plus a round trip
  requests.stop();
  return (Object.keys(LISTS) as ListName[]).filter((name) => requests.find("GET", LISTS[name]).length > 0);
}

/**
 * A background event of the host's own (a health check finishing, say) can land beside the push and make the page read
 * everything once. A push that always read more than it named would read more every time, so one clean answer in three is proof.
 */
async function readAfter(page: Page, reason: string, expected: ListName[]): Promise<ListName[]> {
  let last: ListName[] = [];
  for (let attempt = 0; attempt < 3; attempt += 1) {
    last = await readOnce(page, reason);
    if (JSON.stringify(last) === JSON.stringify(expected)) return last;
  }
  return last;
}

test.describe("what a push of attention-changed reads again", () => {
  test.describe.configure({ mode: "serial" });

  test("each word reads only the lists it names, and a word nobody knows reads everything", async ({ page }) => {
    test.setTimeout(120_000);
    await openScreen(page, "/settings/tags");
    await untilQuiet(page);
    await joinedTheDashboard(page);
    // Let the first read after opening finish, so it is not mistaken for the push's.
    await page.waitForTimeout(1_500); // reason: the screen's own first read of the counts starts 400 ms after it opens

    expect(await readAfter(page, "noRelease", ["summary"]), "noRelease").toEqual(["summary"]);
    expect(await readAfter(page, "update", ["summary"]), "update").toEqual(["summary"]);
    expect(await readAfter(page, "orphans", ["orphans", "summary"]), "orphans").toEqual(["orphans", "summary"]);
    expect(await readAfter(page, "health", ["health", "summary"]), "health").toEqual(["health", "summary"]);
    expect(await readAfter(page, "jobs", ["failedJobs", "summary"]), "jobs").toEqual(["failedJobs", "summary"]);
    expect(await readAfter(page, "handoff", ["failedJobs", "handoffs", "summary"]), "handoff").toEqual(["failedJobs", "handoffs", "summary"]);
    expect(await readAfter(page, "recovery", ["movieRecovery", "seriesRecovery", "failedJobs", "summary"]), "recovery").toEqual(["movieRecovery", "seriesRecovery", "failedJobs", "summary"]);
    expect(await readAfter(page, "heldReleases", []), "heldReleases").toEqual([]);

    const everything = await readOnce(page, "a-word-nobody-knows");
    for (const name of ["failedJobs", "indexers", "movieWanted", "movieRecovery", "seriesRecovery", "dispatches", "handoffs", "health", "summary"] as ListName[]) {
      expect(everything, `an unknown word also reads ${name}`).toContain(name);
    }
  });
});

test.describe("System > API Access", () => {
  test("makes a key named External automation with the automation access, shows it once with Copy, lists it, and Revoke (after asking) stops it working", async ({ page, context }) => {
    test.setTimeout(90_000);
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    await openScreen(page, "/system/api");
    await untilQuiet(page);

    // The form starts with its defaults.
    await expect(page.getByRole("textbox", { name: "Key name" })).toHaveValue("External automation");
    await expect(page.getByRole("heading", { name: "Active keys" }).or(page.getByText("Active keys")).first()).toBeVisible();

    await page.getByRole("button", { name: "Generate API key" }).click();

    // Shown once, in full, with a Copy button.
    await expect(page.getByText("Key created")).toBeVisible();
    const secret = (await page.locator("code", { hasText: /^deluno_/ }).first().innerText()).trim();
    expect(secret).toMatch(/^deluno_\S{16,}$/);
    await page.getByRole("button", { name: "Copy", exact: true }).click();
    await expect(page.getByText("Copied to clipboard")).toBeVisible();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(secret);

    // It works as the automation key it was made as.
    const manifest = await page.request.get("/api/integrations/external/manifest", { headers: { "X-Api-Key": secret } });
    expect(manifest.status()).toBe(200);

    // It is listed among the active keys, by name and prefix, never by its secret.
    const keys = (await api<Array<{ id: string; name: string; prefix: string; scopes: string }>>(page, "GET", "/api/api-keys")).body;
    const mine = keys.filter((key) => key.name === "External automation").at(-1);
    expect(mine, "the new key is in the list the server keeps").toBeTruthy();
    await expect(page.getByText(mine!.prefix).first()).toBeVisible();

    // Reloading does not show the secret again.
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText("Key created")).toHaveCount(0);
    await expect(page.getByText(secret)).toHaveCount(0);

    // Revoke asks first; saying no keeps the key, saying yes ends it.
    const row = page.getByRole("row").filter({ hasText: mine!.prefix }).or(page.locator("li, div").filter({ hasText: mine!.prefix }).filter({ has: page.getByRole("button", { name: "Revoke" }) })).last();
    await row.getByRole("button", { name: "Revoke" }).click();
    const dialog = page.getByRole("alertdialog").or(page.getByRole("dialog")).filter({ hasText: "Revoke \"External automation\"?" });
    await expect(dialog).toBeVisible();
    await dialog.getByRole("button", { name: /Cancel/ }).click();
    expect((await page.request.get("/api/integrations/external/manifest", { headers: { "X-Api-Key": secret } })).status()).toBe(200);

    await row.getByRole("button", { name: "Revoke" }).click();
    await dialog.getByRole("button", { name: "Revoke key" }).click();
    await expect(page.getByText("API key revoked")).toBeVisible();
    await expect.poll(async () => (await page.request.get("/api/integrations/external/manifest", { headers: { "X-Api-Key": secret } })).status()).toBe(401);
    const after = (await api<Array<{ id: string }>>(page, "GET", "/api/api-keys")).body;
    expect(after.some((key) => key.id === mine!.id)).toBe(false);
  });
});

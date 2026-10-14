import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// rc.30.5 clean-install run, #1132 and #1141:
//  - "Create Weir account and connect" in the guided setup reloaded the page and did nothing (its form sat inside the
//    step's own form);
//  - the Yes/No answers were not in the saved draft, so a reload lost them and the Start page warned about the
//    wrong things.
// The installer is not in the smoke host, so the installed-apps list and Weir's connect answer are the stubbed things;
// every click and read is on the real page.

const json = (body: unknown) => ({ status: 200, contentType: "application/json", body: JSON.stringify(body) });

const weirNeedsAnAccount = {
  kind: "weir", displayName: "Weir", category: "processor", installed: true, reachable: true,
  defaultHost: "127.0.0.1", defaultPort: 9347, needsAccount: true, accountExists: false
};

/** The draft lives in memory for this test only, so it neither reads nor leaves another test's values. */
async function serveAnInMemoryDraft(page: Page) {
  let draft: Record<string, unknown> = {
    mode: "simple", mediaIntent: "both", movieRootPath: "D:\\Media\\Movies", seriesRootPath: "D:\\Media\\TV", downloadsPath: "",
    qualityPreset: "balanced1080p", formatGoal: "balanced", indexerName: "", indexerProtocol: "torznab", indexerUrl: "",
    clientName: "", clientProtocol: "qbittorrent", clientHost: "", clientPort: "8080",
    metadataProviderMode: "direct", metadataBrokerUrl: "", backupEnabled: true,
    firstTitleType: "movies", firstTitle: "", firstTitleYear: "", firstTitleMonitored: true,
    updatedUtc: new Date(0).toISOString()
  };
  await page.route("**/api/setup/draft", async (route) => {
    const request = route.request();
    if (request.method() === "PUT") draft = { ...draft, ...request.postDataJSON() };
    await route.fulfill(json(draft));
  });
}

test("Create Weir account and connect works from the guided setup and does not reload the page (#1132)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The same step on a phone is the same form; the desktop run proves the click.");
  await serveAnInMemoryDraft(page);
  let connected = false;
  const sent: unknown[] = [];
  await page.route(/\/api\/system\/bundled-components$/, (route) => route.fulfill(json([{ ...weirNeedsAnAccount, connected }])));
  await page.route(/\/api\/system\/bundled-components\/weir\/connect$/, (route) => {
    sent.push(route.request().postDataJSON());
    connected = true;
    return route.fulfill(json({ kind: "weir", name: "Weir", healthy: true, message: "Weir is connected." }));
  });

  await authenticateAndNavigate(page, "/setup-guide");
  await page.getByRole("button", { name: /Connect installed apps/ }).click();
  await page.getByRole("button", { name: "Connect Weir" }).click();

  // A marker on the page: a reload (the old behaviour) wipes it.
  await page.evaluate(() => { (window as unknown as { __notReloaded: boolean }).__notReloaded = true; });
  await page.getByLabel("Weir username").fill("gpweir");
  await page.getByLabel("Weir password", { exact: true }).fill("a-long-weir-pass-1");
  await page.getByLabel("Confirm Weir password").fill("a-long-weir-pass-1");
  await page.getByRole("button", { name: "Create Weir account and connect" }).click();

  await expect(page.getByText("Weir is connected.")).toBeVisible();
  expect(sent).toEqual([{ username: "gpweir", password: "a-long-weir-pass-1" }]);
  expect(await page.evaluate(() => (window as unknown as { __notReloaded?: boolean }).__notReloaded)).toBe(true);
  expect(new URL(page.url()).search).toBe("");
});

test("the guided setup's Yes/No answers survive a reload, and the Start page names only what is still missing (#1132)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The step list is the same on a phone; the desktop run proves the words.");
  await serveAnInMemoryDraft(page);
  await authenticateAndNavigate(page, "/setup-guide");

  await page.getByRole("button", { name: /How you sign in/ }).click();
  await page.getByRole("radio", { name: "Skip sign-in for devices on your home network? No" }).click();
  await page.getByRole("button", { name: /^(?:\d+ )?Updates\b/ }).click();
  await page.getByRole("radio", { name: "Update Deluno automatically? Yes" }).click();
  await page.getByRole("button", { name: /^(?:\d+ )?Automation\b/ }).click();
  // The draft is saved half a second after the last change: wait for that save, not for a guessed time.
  const saved = page.waitForResponse((response) => response.url().includes("/api/setup/draft") && response.request().method() === "PUT", { timeout: 10_000 });
  await page.getByRole("radio", { name: /Yes$/ }).first().click();
  await saved;

  await page.reload();
  await page.getByRole("button", { name: /How you sign in/ }).click();
  await expect(page.getByRole("radio", { name: "Skip sign-in for devices on your home network? No" })).toHaveAttribute("aria-checked", "true");
  await page.getByRole("button", { name: /^(?:\d+ )?Updates\b/ }).click();
  await expect(page.getByRole("radio", { name: "Update Deluno automatically? Yes" })).toHaveAttribute("aria-checked", "true");

  await page.getByRole("button", { name: /^(?:\d+ )?Start\b/ }).click();
  const main = page.locator("#main-content");
  // The sign-in and update questions are answered, so only the automation questions are left; the library, quality and
  // release choices are made, so they are not named.
  await expect(main.getByText(/Still needed before Deluno can create the baseline/)).toBeVisible();
  const said = (await main.getByText(/Still needed before Deluno can create the baseline/).innerText()).replace(/\s+/g, " ");
  expect(said).toContain("every automation question");
  expect(said).not.toContain("Skip sign-in");
  expect(said).not.toContain("Update automatically");
  expect(said).not.toContain("library root folder");
  expect(said).not.toContain("picture quality");
});

test("with Weir connected the guide asks, per library type, whether to clean files with Weir, and Start waits for the answers (#1158)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The step list is the same on a phone; the desktop run proves the words.");
  await serveAnInMemoryDraft(page);
  await page.route(/\/api\/system\/bundled-components$/, (route) => route.fulfill(json([{ ...weirNeedsAnAccount, needsAccount: false, accountExists: true, connected: true }])));
  await authenticateAndNavigate(page, "/setup-guide");

  await page.getByRole("button", { name: /Connect installed apps/ }).click();
  const main = page.locator("#main-content");
  const movies = "Movies: Clean files with Weir before they go into your library?";
  const tv = "TV: Clean files with Weir before they go into your library?";
  // Nothing is chosen for the person (#936).
  await expect(main.getByRole("radio", { name: `${movies} Yes` })).toHaveAttribute("aria-checked", "false");
  await expect(main.getByRole("radio", { name: `${movies} No` })).toHaveAttribute("aria-checked", "false");
  await expect(main.getByRole("radio", { name: `${tv} Yes` })).toBeVisible();

  // A Yes asks where Weir leaves the cleaned files, with Weir's own folder suggested; a No asks nothing more.
  await main.getByRole("radio", { name: `${movies} Yes` }).click();
  await expect(main.getByLabel("Where Weir leaves cleaned Movies files")).toHaveValue("D:\\Weir\\Ready\\Movies");
  await main.getByRole("radio", { name: `${tv} No` }).click();
  await expect(main.getByLabel("Where Weir leaves cleaned TV files")).toHaveCount(0);

  // Start names what is still open: the automation questions, not the Weir ones that are answered.
  await page.getByRole("button", { name: /^(?:\d+ )?Start\b/ }).click();
  const said = (await main.getByText(/Still needed before Deluno can create the baseline/).innerText()).replace(/\s+/g, " ");
  expect(said).not.toContain("with Weir");
});

import { expect as baseExpect, test } from "@playwright/test";
import { fallbackCredentials } from "../helpers/auth-helper";

// Cause of the flakes (#1191): the default 5 s wait for a heading or a control is shorter than a cold render takes on a busy machine.
const expect = baseExpect.configure({ timeout: 15_000 });

let credentials: { username: string; password: string } | null = null;

test.describe("authenticated app smoke", () => {
  test.beforeAll(async ({ request }) => {
    const statusResponse = await request.get("/api/auth/bootstrap-status");
    const status = statusResponse.ok() ? ((await statusResponse.json()) as { requiresSetup?: boolean }) : { requiresSetup: false };

    if (status.requiresSetup) {
      const bootstrap = await request.post("/api/auth/bootstrap", {
        data: fallbackCredentials
      });
      if (bootstrap.ok()) {
        credentials = fallbackCredentials;
        return;
      }
    }

    const fallbackLogin = await request.post("/api/auth/login", {
      data: {
        username: fallbackCredentials.username,
        password: fallbackCredentials.password
      }
    });
    if (fallbackLogin.ok()) {
      credentials = fallbackCredentials;
      return;
    }

    if (process.env.DELUNO_E2E_USERNAME && process.env.DELUNO_E2E_PASSWORD) {
      credentials = {
        username: process.env.DELUNO_E2E_USERNAME,
        password: process.env.DELUNO_E2E_PASSWORD
      };
    }
  });

  test.beforeEach(async ({ page }) => {
    test.skip(!credentials, "Existing install detected. Set DELUNO_E2E_USERNAME and DELUNO_E2E_PASSWORD to run authenticated route checks against it.");

    // Cause of the flake (#1191): this signs in through the real login form, whose cold render waits on bootstrap-status, and on a
    // busy machine that wait plus the test's own steps outran the 30 s budget ("waiting for getByLabel(/username/i)"). The form is
    // the thing proved here, so it stays; the budget is what changes.
    test.setTimeout(120_000);
    await page.goto("/login");
    await page.getByLabel(/username/i).fill(credentials!.username);
    await page.getByLabel("Password", { exact: true }).fill(credentials!.password);
    await page.getByRole("button", { name: /sign in/i }).click();
    await expect(page).not.toHaveURL(/\/login/);
  });

  test("keeps the browser fetch implementation native after sign-in", async ({ page }) => {
    await expect
      .poll(() => page.evaluate(() => window.fetch.toString()))
      .toContain("[native code]");
  });

  for (const path of [
    "/",
    "/movies",
    "/movies/upgrades",
    "/tv",
    "/tv/upgrades",
    "/collections",
    "/calendar",
    "/indexers",
    "/indexers/indexers",
    "/indexers/download-clients",
    "/indexers/library-routing",
    "/activity",
    "/queue",
    "/settings",
    "/settings/media-management",
    "/settings/processing",
    "/settings/destination-rules",
    "/settings/profiles",
    "/settings/quality",
    "/settings/custom-formats",
    "/settings/release-rules",
    "/settings/lists",
    "/settings/automation",
    "/settings/metadata",
    "/settings/general",
    "/settings/notifications",
    "/settings/migration",
    "/settings/ui",
    "/system",
    "/system/api",
    "/system/docs"
  ]) {
    test(`loads ${path} with a signed-in user`, async ({ page }) => {
      await page.goto(path);
      // The page has to have drawn before "no error" means anything, and the
      // first paint of a lazily loaded route is what a busy machine slows
      // down (#1057), so wait for the content first and only then look for
      // an error, instead of checking for absence against a blank page.
      await expect(page.locator("body")).toContainText(/Deluno|Overview|Movies|Settings|System|Calendar|Activity|Indexers|API/, { timeout: 15_000 });
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      await expect(page.getByText("This area could not load")).toHaveCount(0);
    });
  }

  // #1086: an activity event is named in plain words on every screen that
  // lists one. An internal kind such as `download.vanished.recovered` or
  // `processing.waiting` must never show as text.
  const rawEventCode =
    /\b(job|jobs|movies|media-servers|download-client|download|downloads|processing|processor|filesystem|library|metadata|retention|intake|release|health|tags|trackers|subtitle|connectivity|seerr|movie|series|episode|external|collection|quality|activity)\.[a-z][a-z0-9_-]*(\.[a-z][a-z0-9_-]*)*\b/;

  // A job's source is a code too (#1086): what started it must read in words.
  // The codes the mocked jobs carry, so the check can name them exactly (a
  // hyphenated word alone in a cell is too ordinary to catch with a pattern).
  const rawSourceCode = /(grab-rejected|download-health|queue-derived|existing-library-import|library-media-probe|download-integrity-failed|processor-rejected)/;

  const smokeJobs = [
    ["series.search.season", "Search for a season", "A refused release", "grab-rejected", "completed"],
    ["episode.search", "Search for an episode", "The download health check", "download-health", "failed"],
    ["library.search.tv", "Search the TV library", "A download in the queue", "queue-derived", "completed"],
    ["movies.metadata.refresh", "Refresh film details", "A scan of a folder you already had", "existing-library-import", "completed"]
  ].map(([jobType, label, sourceLabel, source, status], index) => ({
    id: `smoke-job-${index}`, jobType, label, source, sourceLabel, status, payloadJson: "{}", attempts: 1,
    createdUtc: "2026-09-30T00:00:00Z", scheduledUtc: "2026-09-30T00:00:00Z", startedUtc: "2026-09-30T00:00:00Z",
    completedUtc: "2026-09-30T00:01:00Z", leasedUntilUtc: null, workerId: null, lastError: status === "failed" ? "The indexer did not answer." : null,
    // The failed one is about a title, so Activity lists it; the others are background work, which System lists.
    relatedEntityType: status === "failed" ? "movie" : null, relatedEntityId: status === "failed" ? "smoke-movie" : null, idempotencyKey: null, dedupeKey: null, maxAttempts: 3, lastAttemptUtc: null, nextAttemptUtc: null
  }));
  // What the API sends for an event whose stored message began with a job type.
  const smokeEvents = [
    { id: "smoke-event-1", category: "job.completed", label: "Task finished", message: "Search for a season: Best feed candidate is Some.Show.S01.", detailsJson: null, relatedJobId: "smoke-job-0", relatedEntityType: null, relatedEntityId: null, createdUtc: "2026-09-30T00:01:00Z" },
    { id: "smoke-event-2", category: "job.lease-released", label: "Task released", message: "Refresh film details: released after this worker restarted.", detailsJson: null, relatedJobId: "smoke-job-3", relatedEntityType: null, relatedEntityId: null, createdUtc: "2026-09-30T00:02:00Z" }
  ];

  for (const path of ["/", "/activity", "/system", "/system/audit"]) {
    test(`shows no internal event code on ${path}`, async ({ page }) => {
      await mockJobsAndEvents(page);
      await page.goto(path);
      await expect(page.locator("body")).toContainText(/Deluno|Overview|Activity|System/, { timeout: 15_000 });
      // Let the feed and the live list settle before reading the page. Not
      // networkidle: the live connection and the pollers keep the network busy
      // by design (#1081).
      await expect(page.locator("#main-content [aria-busy='true']")).toHaveCount(0, { timeout: 15_000 });
      await page.waitForTimeout(1_500);
      const text = await page.locator("body").innerText();
      expect(text).not.toMatch(rawEventCode);
      expect(text).not.toMatch(rawSourceCode);
    });
  }

  // #1100: a filtered Activity view is a link (Live's Turned down card opens this one), read against the real host.
  test("opens the Activity history from a filtered link and keeps the filters in the address", async ({ page }) => {
    await page.goto("/activity?type=turned-down&since=today&reason=language");
    // The step and the time sit behind "More filters", which counts the two that are narrowing the list.
    await expect(page.getByRole("button", { name: "More filters, 2 on" })).toBeVisible({ timeout: 15_000 });
    await page.getByRole("button", { name: "More filters, 2 on" }).click();
    await expect(page.getByRole("radio", { name: "Turned down" })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByText("Reason: Not in a language you want")).toBeVisible();
    await expect(page.getByRole("radio", { name: "Today" })).toHaveAttribute("aria-checked", "true");
    await page.keyboard.press("Escape");
    await expect(page.getByText("Nothing matches these filters")).toBeVisible();
    await page.getByRole("button", { name: "Clear filters" }).first().click();
    await expect(page).not.toHaveURL(/type=turned-down/);
    await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
  });

  for (const path of ["/activity", "/system"]) {
    test(`names what started each job in words on ${path}`, async ({ page }) => {
      await mockJobsAndEvents(page);
      await page.goto(path === "/activity" ? "/activity?status=failed" : path);
      // The System list hides this column on a phone, so it is enough that the words are on the page.
      await expect(page.getByText(path === "/activity" ? "The download health check" : "A refused release").first()).toBeAttached({ timeout: 15_000 });
      const text = await page.locator("body").innerText();
      expect(text).not.toMatch(rawSourceCode);
    });
  }

  async function mockJobsAndEvents(page: import("@playwright/test").Page) {
    await page.route("**/api/jobs?**", async (route) => {
      const failedOnly = route.request().url().includes("failed=true");
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ items: failedOnly ? smokeJobs.filter((job) => job.status === "failed") : smokeJobs, hasMore: false, nextPageToken: null })
      });
    });
    await page.route("**/api/activity?**", async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ items: smokeEvents, hasMore: false, nextPageToken: null })
      });
    });
  }

});

import { expect, test, type Locator } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * #612: a candidate the grab endpoint will only accept with `force` used to
 * open with a plain, enabled "Send to downloads" — a button that could only
 * ever 400 ("Release requires force override because Deluno classified it as
 * usable, but it does not meet every preference yet."), with the Force
 * toggle sitting collapsed below it. The person had to click, fail, read the
 * error and then find Force by hand.
 *
 * The preview/search response already tells the drawer this before the click
 * (`decisionStatus`, `meetsCutoff`, `qualityDelta` per candidate mirror the
 * backend's own `IsSafeForAutomaticDispatch`), so both detail pages now open
 * a force-required candidate with Send to downloads disabled and Force
 * already expanded, and leave a candidate that does not need force exactly
 * as it was — one click, no detour.
 */

async function authHeaders(page: import("@playwright/test").Page) {
  const token = await page.evaluate(() => sessionStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

// `decisionStatus: "eligible"` with `preferenceEvaluation` present is exactly
// the shape #612 was filed against: a typed candidate labelled "Eligible"
// that still fails `IsSafeForAutomaticDispatch` (only "preferred" passes),
// so the grab endpoint answers 400 unless `force` is set.
const ELIGIBLE_CANDIDATE = {
  releaseName: "Force Upfront Eligible Release",
  indexerId: "smoke-indexer",
  indexerName: "Smoke Indexer",
  quality: "WEB 1080p",
  score: 0,
  meetsCutoff: true,
  summary: "Passed every hard gate; usable, but it does not meet every preference yet.",
  downloadUrl: "https://example.test/download/eligible",
  sizeBytes: 2147483648,
  seeders: 10,
  decisionStatus: "eligible",
  decisionReasons: ["Meets the target quality", "Missing a preferred custom format"],
  riskFlags: [],
  qualityDelta: 0,
  preferenceEvaluation: { typed: true }
};

// Deluno's own pick: preferred, cutoff met, no downgrade. The one candidate
// `IsSafeForAutomaticDispatch` already accepts without force, so the plain
// "Send to downloads" click must keep working exactly as before.
const PREFERRED_CANDIDATE = {
  releaseName: "Force Upfront Preferred Release",
  indexerId: "smoke-indexer",
  indexerName: "Smoke Indexer",
  quality: "WEB 1080p",
  score: 0,
  meetsCutoff: true,
  summary: "Deluno's preferred candidate for this title.",
  downloadUrl: "https://example.test/download/preferred",
  sizeBytes: 2147483648,
  seeders: 40,
  decisionStatus: "preferred",
  decisionReasons: ["Best match for this Library Profile"],
  riskFlags: [],
  qualityDelta: 0,
  preferenceEvaluation: { typed: true }
};

function searchPreviewBody() {
  return JSON.stringify({
    outcome: "held",
    summary: "Deluno compared 2 candidates.",
    candidates: [ELIGIBLE_CANDIDATE, PREFERRED_CANDIDATE]
  });
}

async function assertForceRequiredUpfront(dialog: Locator) {
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Send to downloads", exact: true })).toBeDisabled();
  await expect(
    dialog.getByText("Deluno did not pick this release automatically (Eligible). Force it below to send anyway.")
  ).toBeVisible();
  // Expanded already: the reason field and "Send anyway" are on screen
  // without an extra click, and the collapsed toggle never appears to hunt
  // for.
  await expect(dialog.getByLabel("Why force this release?")).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Send anyway", exact: true })).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Force", exact: true })).toHaveCount(0);
}

async function assertNormalOneClickPath(dialog: Locator) {
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Send to downloads", exact: true })).toBeEnabled();
  // Untouched: Force stays collapsed and optional, not pre-expanded.
  await expect(dialog.getByLabel("Why force this release?")).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Force", exact: true })).toBeVisible();
}

test.describe("choose a release shows force up front (#612)", () => {
  test("movie drawer disables Send to downloads and expands Force only for the candidate that needs it", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");
    const headers = await authHeaders(page);
    const title = `Force upfront movie ${Date.now()}`;
    const create = await page.request.post("/api/movies/", {
      headers,
      data: { title, releaseYear: 2024, monitored: true }
    });
    expect(create.ok()).toBe(true);
    const movie = await create.json() as { id: string };

    await page.route(`**/api/movies/${movie.id}/search**`, async (route) => {
      await route.fulfill({ contentType: "application/json", body: searchPreviewBody() });
    });

    try {
      await page.goto(`/movies/${movie.id}`);
      await page.getByRole("button", { name: "Choose a release", exact: true }).click();

      await page.getByRole("row").filter({ hasText: ELIGIBLE_CANDIDATE.releaseName }).click();
      await assertForceRequiredUpfront(page.getByRole("dialog", { name: ELIGIBLE_CANDIDATE.releaseName }));
      await page.keyboard.press("Escape");

      await page.getByRole("row").filter({ hasText: PREFERRED_CANDIDATE.releaseName }).click();
      await assertNormalOneClickPath(page.getByRole("dialog", { name: PREFERRED_CANDIDATE.releaseName }));
    } finally {
      const cleanup = await page.request.post("/api/movies/bulk", {
        headers,
        data: { movieIds: [movie.id], operation: "remove" },
        timeout: 5_000
      });
      expect(cleanup.ok(), `POST /api/movies/bulk cleanup failed: ${cleanup.status()}`).toBe(true);
    }
  });

  test("show drawer disables Send to downloads and expands Force only for the candidate that needs it", async ({ page }) => {
    await authenticateAndNavigate(page, "/tv");
    const headers = await authHeaders(page);
    const title = `Force upfront show ${Date.now()}`;
    const create = await page.request.post("/api/series/", {
      headers,
      data: { title, startYear: 2024, monitored: true }
    });
    expect(create.ok()).toBe(true);
    const series = await create.json() as { id: string };

    await page.route(`**/api/series/${series.id}/search**`, async (route) => {
      await route.fulfill({ contentType: "application/json", body: searchPreviewBody() });
    });

    try {
      await page.goto(`/tv/${series.id}`);
      await page.getByRole("button", { name: "Choose a release", exact: true }).click();

      await page.getByRole("row").filter({ hasText: ELIGIBLE_CANDIDATE.releaseName }).click();
      await assertForceRequiredUpfront(page.getByRole("dialog", { name: ELIGIBLE_CANDIDATE.releaseName }));
      await page.keyboard.press("Escape");

      await page.getByRole("row").filter({ hasText: PREFERRED_CANDIDATE.releaseName }).click();
      await assertNormalOneClickPath(page.getByRole("dialog", { name: PREFERRED_CANDIDATE.releaseName }));
    } finally {
      const cleanup = await page.request.post("/api/series/bulk", {
        headers,
        data: { seriesIds: [series.id], operation: "remove" },
        timeout: 5_000
      });
      expect(cleanup.ok(), `POST /api/series/bulk cleanup failed: ${cleanup.status()}`).toBe(true);
    }
  });
});

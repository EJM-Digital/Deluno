import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * #1175: sending a release by hand for a title that already has a download on its way asks
 * first (Replace it / Send as well / Cancel) instead of quietly sending a second copy, and the
 * answer goes back with the next send.
 */

async function authHeaders(page: import("@playwright/test").Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

const CANDIDATE = {
  releaseName: "Ask First Preferred Release",
  indexerId: "smoke-indexer",
  indexerName: "Smoke Indexer",
  quality: "WEB 1080p",
  score: 0,
  meetsCutoff: true,
  summary: "Deluno's preferred candidate for this title.",
  downloadUrl: "https://example.test/download/ask-first",
  sizeBytes: 2147483648,
  seeders: 40,
  decisionStatus: "preferred",
  decisionReasons: ["Best match for this Library Profile"],
  riskFlags: [],
  qualityDelta: 0,
  preferenceEvaluation: { typed: true }
};

test.describe("a hand send for something already downloading asks first (#1175)", () => {
  test("the movie drawer asks, and Send as well sends with that answer", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");
    const headers = await authHeaders(page);
    const create = await page.request.post("/api/movies/", {
      headers,
      data: { title: `Ask first movie ${Date.now()}`, releaseYear: 2024, monitored: true }
    });
    expect(create.ok()).toBe(true);
    const movie = (await create.json()) as { id: string };

    await page.route(`**/api/movies/${movie.id}/search**`, async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ outcome: "held", summary: "Deluno compared 1 candidate.", candidates: [CANDIDATE] })
      });
    });
    const sent: Array<{ duplicateAction: string | null }> = [];
    await page.route(`**/api/movies/${movie.id}/grab**`, async (route) => {
      const body = route.request().postDataJSON() as { duplicateAction: string | null };
      sent.push({ duplicateAction: body.duplicateAction });
      if (!body.duplicateAction) {
        await route.fulfill({
          status: 409,
          contentType: "application/json",
          body: JSON.stringify({
            code: "already-downloading",
            message: "This film is already downloading in qBittorrent. Send this one as well?"
          })
        });
        return;
      }
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({ releaseName: CANDIDATE.releaseName, dispatchStatus: "sent", dispatchMessage: "Sent." })
      });
    });

    try {
      await page.goto(`/movies/${movie.id}`);
      await page.getByRole("button", { name: "Choose a release", exact: true }).click();
      await page.getByRole("row").filter({ hasText: CANDIDATE.releaseName }).click();
      await page.getByRole("dialog", { name: CANDIDATE.releaseName }).getByRole("button", { name: "Send to downloads", exact: true }).click();

      const question = page.getByRole("dialog", { name: "Already downloading" });
      await expect(question).toBeVisible();
      await expect(question.getByText("This film is already downloading in qBittorrent. Send this one as well?")).toBeVisible();
      await expect(question.getByRole("button", { name: "Replace it", exact: true })).toBeVisible();
      await expect(question.getByRole("button", { name: "Cancel", exact: true })).toBeVisible();

      await question.getByRole("button", { name: "Send as well", exact: true }).click();
      await expect.poll(() => sent.map((item) => item.duplicateAction)).toEqual([null, "send-as-well"]);
    } finally {
      const cleanup = await page.request.post("/api/movies/bulk", {
        headers,
        data: { movieIds: [movie.id], operation: "remove" },
        timeout: 5_000
      });
      expect(cleanup.ok()).toBe(true);
    }
  });
});

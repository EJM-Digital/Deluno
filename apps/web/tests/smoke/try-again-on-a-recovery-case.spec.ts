import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #558. Try again on a recovery case posted to a route that did not exist, so
// every press answered 404 and "That import could not be tried again". Nothing
// pressed it. Now the server acts on the kind of case, or says why it cannot yet,
// and In progress shows what it said. A quality problem needs a decision, not
// another attempt, so it offers no Try again at all.
const lostDownload = {
  id: "case-lost-download",
  title: "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
  failureKind: "downloadFailed",
  summary: "Download was sent to SABnzbd but never appeared in the client's queue after 2 hours.",
  recommendedAction: "Check if the download client is running and properly connected.",
  detailsJson: JSON.stringify({ DispatchId: "dispatch-lost" }),
  detectedUtc: "2026-09-22T03:10:10Z"
};

const qualityProblem = {
  ...lostDownload,
  id: "case-quality",
  title: "Sintel.2010.720p.WEB-DL-DELUNO",
  failureKind: "quality",
  summary: "The file is below the library's cutoff.",
  recommendedAction: "Decide whether to keep it."
};

function summary(cases: unknown[]) {
  return { openCount: cases.length, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: cases.length, importFailedCount: 0, recentCases: cases };
}

async function showCases(page: Page, answer: { status: number; message: string }) {
  const posted: string[] = [];
  // The screens ask with ?limit= (#1081), so the query string is allowed.
  await page.route(/\/api\/movies\/import-recovery(\?.*)?$/, async (route) => {
    await route.fulfill({ contentType: "application/json", body: JSON.stringify(summary([lostDownload, qualityProblem])) });
  });
  await page.route(/\/api\/series\/import-recovery(\?.*)?$/, async (route) => {
    await route.fulfill({ contentType: "application/json", body: JSON.stringify(summary([])) });
  });
  await page.route("**/api/movies/import-recovery/*/retry", async (route) => {
    posted.push(new URL(route.request().url()).pathname);
    await route.fulfill({ status: answer.status, contentType: "application/json", body: JSON.stringify({ message: answer.message }) });
  });
  return posted;
}

test("Try again is offered where Deluno can act, and says what it did", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  const posted = await showCases(page, { status: 200, message: "Deluno ended the download SABnzbd never showed and is searching for Tears of Steel again." });

  await page.goto("/queue");
  const lostRow = page.getByRole("row").filter({ hasText: lostDownload.title });
  const qualityRow = page.getByRole("row").filter({ hasText: qualityProblem.title });
  await expect(lostRow.getByRole("button", { name: "Try again" })).toBeVisible();
  await expect(qualityRow.getByRole("button", { name: "Dismiss" })).toBeVisible();
  await expect(qualityRow.getByRole("button", { name: "Try again" })).toHaveCount(0);

  await lostRow.getByRole("button", { name: "Try again" }).click();

  await expect(page.getByText("is searching for Tears of Steel again")).toBeVisible();
  expect(posted).toEqual([`/api/movies/import-recovery/${lostDownload.id}/retry`]);
});

test("Try again that cannot act yet says why instead of failing blankly", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  await showCases(page, { status: 409, message: "Weir still has Tears.of.Steel, so there is nothing to try again until it finishes or gives up." });

  await page.goto("/queue");
  await page.getByRole("row").filter({ hasText: lostDownload.title }).getByRole("button", { name: "Try again" }).click();

  await expect(page.getByText("Weir still has Tears.of.Steel")).toBeVisible();
  await expect(page.getByText("That could not be tried again.")).toHaveCount(0);
});

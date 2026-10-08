import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1180: a show-wide search queues one episode search per episode, and Activity listed twenty
// identical "Search for an episode" rows. Each row now names its show and episode, a show-wide
// search reads as one entry with its counts, the episodes open inside it, and the counts follow
// a JobChanged push with no reload.

const episodeJob = (episode: number, status: string) => ({
  id: `job-ep-${episode}`,
  jobType: "episode.search",
  label: "Search for an episode",
  source: "series",
  sourceLabel: "The TV library",
  status,
  payloadJson: null,
  attempts: 1,
  createdUtc: "2026-10-08T02:00:00Z",
  scheduledUtc: "2026-10-08T02:00:00Z",
  startedUtc: status === "queued" ? null : "2026-10-08T02:00:05Z",
  completedUtc: status === "completed" ? "2026-10-08T02:00:30Z" : null,
  leasedUntilUtc: null,
  workerId: null,
  lastError: null,
  relatedEntityType: "episode",
  relatedEntityId: `ep-${episode}`,
  idempotencyKey: null,
  dedupeKey: null,
  maxAttempts: 3,
  lastAttemptUtc: null,
  nextAttemptUtc: null,
  target: `Severance S02E${String(episode).padStart(2, "0")}`,
  showId: "show-severance",
  showTitle: "Severance"
});

const soloJob = { ...episodeJob(1, "queued"), id: "job-solo", relatedEntityId: "ep-solo", target: "Andor S01E03", showId: "show-andor", showTitle: "Andor" };

async function pushJobChanged(page: Page, jobId: string, state: string) {
  const status = await page.evaluate(async ({ jobId, state }) => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch("/api/testing/realtime/live-event", {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: JSON.stringify({ eventName: "JobChanged", payload: { jobId, jobType: "episode.search", state, label: "Search for an episode", libraryId: null, message: null, at: new Date().toISOString() } })
    });
    return response.status;
  }, { jobId, state });
  expect(status).toBe(204);
}

test("a show-wide search reads as one named entry whose counts follow the server's pushes", async ({ page }) => {
  test.setTimeout(90_000);
  await authenticateAndNavigate(page, "/");

  // Ten episodes of one show: three done, one running, six waiting; plus one lone search for another show.
  let statuses: Record<number, string> = {};
  for (let episode = 1; episode <= 10; episode += 1) statuses[episode] = episode <= 3 ? "completed" : episode === 4 ? "running" : "queued";
  await page.route("**/api/jobs?**", async (route) => {
    const failedOnly = new URL(route.request().url()).searchParams.get("failed") === "true";
    const items = failedOnly ? [] : [soloJob, ...Object.entries(statuses).map(([episode, status]) => episodeJob(Number(episode), status))];
    await route.fulfill({ contentType: "application/json", body: JSON.stringify({ items, nextPageToken: null, hasMore: false }) });
  });

  await page.goto("/activity");

  // One grouped entry, not ten identical rows.
  await expect(page.getByText("Search for an episode")).toHaveCount(1); // the lone search's step line
  await expect(page.getByText("Andor S01E03")).toBeVisible();
  const group = page.getByText("10 episodes · 3 done · 1 running · 6 waiting");
  await expect(group).toBeVisible();
  await expect(page.getByText("Severance S02E05")).toHaveCount(0);

  // The counts change when the server says a job changed, with no reload.
  let navigations = 0;
  page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) navigations += 1; });
  await expect.poll(async () => (await page.evaluate(async () => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch("/api/testing/realtime/watchers", { headers: { Authorization: `Bearer ${token}` } });
    return (await response.json()).live as number;
  })), { timeout: 15_000 }).toBeGreaterThan(0);
  statuses = { ...statuses, 4: "completed", 5: "running" };
  await pushJobChanged(page, "job-ep-4", "completed");
  await expect(page.getByText("10 episodes · 4 done · 1 running · 5 waiting")).toBeVisible({ timeout: 10_000 });
  expect(navigations, "the page reloaded or navigated").toBe(0);

  // The episodes stay reachable inside the entry, each named, and open as a job.
  await page.getByText("10 episodes · 4 done · 1 running · 5 waiting").click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByRole("button", { name: /Severance S02E10/ })).toBeVisible();
  await expect(dialog.getByRole("button", { name: /Severance S02E01/ })).toBeVisible();
  await dialog.getByRole("button", { name: /Severance S02E10/ }).click();
  await expect(page.getByRole("dialog").getByText("Severance S02E10").first()).toBeVisible();
});

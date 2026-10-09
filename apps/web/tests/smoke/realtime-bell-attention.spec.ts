import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1179: a dismiss made elsewhere (another tab or device) moves the open bell with no reload and no timer.
// The attention summary (#1185) is stood in for by a route that lists the titles still wanted; the dismiss itself goes
// to the real server, which pushes AttentionChanged after the write succeeds, and the bell re-reads only the summary.

/** Calls the API as the signed-in user, the way a second tab would. */
async function asAnotherTab(page: Page, path: string) {
  return page.evaluate(async (path) => {
    const token = localStorage.getItem("deluno-auth-token");
    const response = await fetch(path, { method: "POST", headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, body: "{}" });
    return response.status;
  }, path);
}

function row(id: string, title: string) {
  return { mediaType: "movie", id, title, year: 1972, libraryId: "probe-library", lastSearchUtc: "2026-10-08T00:00:00Z", missingSinceUtc: null, reason: null };
}

/** The summary the server would build for these titles: one media row each, and the count they add up to. */
function summaryOf(titles: ReturnType<typeof row>[]) {
  const rows = titles.map((title) => ({
    id: `no-release:${title.mediaType}:${title.id}`, section: "media", kind: "no-release", tone: "warn", title: title.title,
    text: "No acceptable release yet: every search found nothing Deluno would take.", to: `/movies/${title.id}`, action: "Open Wanted",
    count: 1, sinceUtc: null, data: title
  }));
  return { count: rows.length, rows, notices: [], leftovers: 0, suggestions: 0 };
}

test.describe("the open bell follows a dismiss made elsewhere (#1179)", () => {
  test("a no-release row dismissed through the API leaves the open bell and lowers the count, with no reload", async ({ page }) => {
    test.setTimeout(90_000);
    const listed = [row("probe-clown", "Probe Film One"), row("probe-other", "Probe Film Two")];
    await page.route("**/api/attention/summary", async (route) => {
      await route.fulfill({ contentType: "application/json", body: JSON.stringify(summaryOf(listed)) });
    });

    await authenticateAndNavigate(page, "/system");
    let navigations = 0;
    page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) navigations += 1; });
    const bell = page.getByRole("button", { name: /^Notifications/ });
    await expect(bell).toHaveAccessibleName(/\d+ things? needs? a look/, { timeout: 20_000 });
    const countOf = async () => Number(/(\d+) things? needs? a look/.exec((await bell.getAttribute("aria-label")) ?? (await bell.innerText()))?.[1] ?? "0");
    const before = await countOf();
    expect(before).toBe(2);
    const navigationsAfterLoad = navigations;

    await bell.click();
    await expect(page.getByText("Probe Film One").first()).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("Probe Film Two").first()).toBeVisible();

    // Cause of the flake (#1191): the dismiss was announced as soon as the list showed, before this page had joined the
    // subject the announcement goes to (the connection opens after the first paint), so nothing reached the bell.
    await expect.poll(async () => page.evaluate(async () => {
      const token = localStorage.getItem("deluno-auth-token");
      const response = await fetch("/api/testing/realtime/watchers", { headers: { Authorization: `Bearer ${token}` } });
      return ((await response.json()) as { dashboard: number }).dashboard;
    }), { timeout: 20_000, message: "the page never joined the dashboard subject" }).toBeGreaterThan(0);

    // Another tab dismisses the first title: the server stops listing it and pushes.
    listed.shift();
    expect(await asAnotherTab(page, "/api/attention/no-release/movie/probe-clown/dismiss")).toBe(204);

    await expect(page.getByText("Probe Film One")).toHaveCount(0, { timeout: 15_000 });
    await expect(page.getByText("Probe Film Two").first()).toBeVisible();
    await expect.poll(countOf, { timeout: 15_000 }).toBe(before - 1);
    expect(navigations, "the page reloaded or navigated").toBe(navigationsAfterLoad);
  });
});

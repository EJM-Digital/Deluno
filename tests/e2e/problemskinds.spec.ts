import { expect, test } from "@playwright/test";
import { openDeluno, sendFilm, sleep, telemetryRows } from "./flow.ts";
import { containerOf, docker, makeFilm, routeLibraryTo, setUpStack, until, type Stack } from "./stack.ts";

/**
 * Group `problemskinds` (#1288, the queue area's "dismiss everywhere for the other kinds of problem"; `problems` covers a failed hand-off): a
 * held release and a leftover folder, each a problem Transfers counts, each shown in several places and each dismissed from the button on the
 * Transfers screen itself (not through the API). A leftover stands in Transfers (Needs attention) and in the bell (the attention summary); a
 * held release is Transfers' own (the bell lists only what no other screen does), in its list and on the open screen. Once dismissed it is
 * gone from every place within 15 seconds, with the screen open and nothing reloaded.
 *   - held release: a search finds a release that is usable but below the profile's cutoff, so Deluno holds it for a person's yes instead of
 *     sending it (the profile's cutoff is raised for the run and put back, and its size rules are loosened to one megabyte so the small test
 *     release is not refused for its size);
 *   - leftover folder: a folder in the movies download folder that no client, import or hand-off is using, old enough to count (made inside the
 *     client's container, dated years ago); "Keep" is the button, and the folder stays on disk.
 * The failed import job, recovery case and failed download kinds are proven in their own groups (removal, tryagain, resend).
 */
test.describe.configure({ mode: "serial" });

let stack: Stack;

const standing = (places: Record<string, boolean>) => Object.entries(places).filter(([, on]) => on).map(([name]) => name);

async function summaryText() {
  const r = await stack.deluno.call("GET", "/api/attention/summary");
  return r.status === 200 ? r.text : "";
}

test.beforeAll(async () => {
  test.setTimeout(10 * 60_000);
  stack = await setUpStack();
});

test("problems.dismiss-everywhere.leftover: A leftover folder stands in Transfers and the bell and clears from both when Keep is pressed on the Transfers screen", async ({ browser }) => {
  test.setTimeout(8 * 60_000);
  // A leftover is what a release Deluno grabbed left behind once nothing uses it: Deluno sends a release that no one serves (so the client holds
  // it and nothing is downloaded), the person tells Deluno to stop tracking it and takes it out of the client, and the folder it would have
  // finished into is on disk, old enough to count. (The scan only lists folders named like a release Deluno grabbed.)
  const sent = await sendFilm(stack, "qbittorrent", "Leftover", "file", { variant: "dummy" });
  const name = sent.film.release;
  const folder = `/downloads/completed/Movies/${name}`;
  const run = (script: string) => docker("exec", "-u", "1654", containerOf("qbittorrent"), "sh", "-c", script);
  const row = (await until(`Transfers to list ${name}`, 120, async () => (await telemetryRows(stack)).find((r) => String(r.releaseName ?? "").includes(name)), 1000)).value;
  const forget = await stack.deluno.call<any>("POST", `/api/download-clients/${row.clientId}/queue/${encodeURIComponent(row.id)}/remove`, { reason: "forget" });
  expect(forget.status === 200 && forget.body?.succeeded, `Deluno stops tracking the send (HTTP ${forget.status}: ${forget.text.slice(0, 200)})`).toBeTruthy();
  await stack.qbittorrent.post("/api/v2/torrents/delete", { hashes: sent.film.infohash.toLowerCase(), deleteFiles: "false" });
  await until("qBittorrent to let go of the torrent", 30, async () => !(await stack.qbittorrent.torrent(sent.film.infohash)), 1000);
  let before = 0;
  try { before = JSON.parse(await summaryText() || "{}").leftovers ?? 0; } catch { /* no summary yet */ }
  // The folder holds a few megabytes; dated 2000 so it is old enough however the minimum age is set.
  run(`mkdir -p '${folder}' && head -c 4000000 /dev/zero > '${folder}/${name}.mkv' && touch -t 200001010000 '${folder}/${name}.mkv' '${folder}'`);
  const page = await openDeluno(browser, stack, "/queue");
  try {
    const places = async (): Promise<Record<string, boolean>> => {
      const orphans = await stack.deluno.call("GET", "/api/download-clients/orphans");
      const summary = await summaryText();
      const parsed = (() => { try { return JSON.parse(summary); } catch { return {}; } })();
      return {
        "Transfers: Needs attention (leftover folders)": orphans.status === 200 && orphans.text.includes(name),
        "The bell (attention summary)": summary.includes(name) || (parsed.leftovers ?? 0) > before
      };
    };
    // The leftover scan runs every minute.
    const shown = await until("the leftover folder to stand in at least two places", 240, async () => {
      const p = await places();
      return standing(p).length >= 2 ? p : undefined;
    }, 3000).catch(() => undefined);
    const stood = shown?.value ?? await places();
    expect(standing(stood).length, `The leftover stands in two places: ${standing(stood).join(" | ")}`).toBeGreaterThanOrEqual(2);

    // The row is on the open screen (it follows the server, so no reload is needed to see it).
    const keep = page.getByRole("button", { name: `Keep ${name}` });
    await expect(keep, "The Transfers screen lists the leftover with its Keep button").toBeVisible({ timeout: 60_000 });
    const t0 = Date.now();
    await keep.click();
    await expect(keep, "The row leaves the Transfers screen without a reload").toBeHidden({ timeout: 15_000 });
    const clear = await until("every place to clear", 15, async () => (standing(await places()).length === 0 ? true : undefined), 500).catch(() => undefined);
    expect(clear !== undefined, `Every place clears within 15 s (${clear ? `after ${((Date.now() - t0) / 1000).toFixed(1)} s` : `still in: ${standing(await places()).join(" | ")}`})`).toBe(true);
    // Keep is a dismissal: the folder and its film are still on disk.
    expect(run(`ls '${folder}' | wc -l`).trim(), "Keep leaves the folder's files where they are").toBe("1");
  } finally {
    await page.context().close();
    try { run(`rm -rf '${folder}'`); } catch { /* already gone */ }
  }
});

test("problems.dismiss-everywhere.held: A held release stands in Transfers' list and on its open screen and clears from both when Dismiss is pressed on the Transfers screen", async ({ browser }) => {
  test.setTimeout(8 * 60_000);
  const library = (await stack.deluno.must<any[]>("GET", "/api/libraries")).find((l) => l.id === stack.libraryId)!;
  const profile = (await stack.deluno.must<any[]>("GET", "/api/quality-profiles")).find((p) => p.id === library.qualityProfileId)!;
  const tiers = ((await stack.deluno.must<any>("GET", "/api/quality-model")).tiers as any[]).map((t) => String(t.name));
  const original = { cutoffQuality: profile.cutoffQuality, sizeRules: profile.sizeRules };
  const put = (body: { cutoffQuality: string; sizeRules: unknown }) => stack.deluno.must("PUT", `/api/quality-profiles/${profile.id}`, {
    name: profile.name, cutoffQuality: body.cutoffQuality, allowedQualities: profile.allowedQualities, customFormatIds: profile.customFormatIds,
    upgradeUntilCutoff: profile.upgradeUntilCutoff, upgradeUnknownItems: profile.upgradeUnknownItems, sizeRules: body.sizeRules
  });
  const higher = tiers.find((t) => /bluray.*1080/i.test(t)) ?? tiers[0];
  let page: Awaited<ReturnType<typeof openDeluno>> | undefined;
  try {
    // A cutoff above what the feed offers (WEB 1080p) and a size floor of one megabyte: the release is usable, but not good enough to send on its own.
    const loose = String(profile.allowedQualities).split(",").map((quality) => ({ quality: quality.trim(), minGb: 0, maxGb: 0, minMb: 1, maxMb: 0 }));
    await put({ cutoffQuality: higher, sizeRules: loose });
    const title = `E2eHeld${Date.now().toString(36)}`;
    const film = await makeFilm(title, false, false, "file");
    const movie = await stack.deluno.must<any>("POST", "/api/movies", { title: film.title, releaseYear: film.year, imdbId: null, monitored: true });
    await routeLibraryTo(stack, "qbittorrent");
    const searched = await stack.deluno.must<any>("POST", `/api/movies/${movie.id}/search`);
    expect(searched.dispatchStatus, `The search sent nothing (it said: ${searched.summary})`).toBeFalsy();
    const held = (await until("Deluno to hold the release", 60, async () => {
      const rows = await stack.deluno.must<any[]>("GET", "/api/attention/held-releases");
      return rows.find((r) => String(r.releaseName).includes(film.release));
    }, 2000)).value;

    page = await openDeluno(browser, stack, "/queue");
    const dismiss = page.getByRole("button", { name: `Dismiss ${film.release}` });
    // A held release is Transfers' own list (the bell lists only what no other screen does), so it stands in the list the screen reads and in
    // the row on the open screen, which follows the server with no reload.
    const places = async (): Promise<Record<string, boolean>> => {
      const rows = await stack.deluno.must<any[]>("GET", "/api/attention/held-releases");
      return {
        "Transfers: the held-release list": rows.some((r) => String(r.releaseName).includes(film.release)),
        "Transfers: the row on the open screen": (await dismiss.count()) > 0
      };
    };
    await expect(dismiss, "The Transfers screen lists the held release with its Dismiss button").toBeVisible({ timeout: 60_000 });
    const stood = await places();
    expect(stood["Transfers: the held-release list"], `The release is held for a yes: ${JSON.stringify(held).slice(0, 300)}`).toBe(true);
    expect(standing(stood).length, `The held release stands in two places: ${standing(stood).join(" | ")}`).toBe(2);
    expect(await summaryText(), "The bell does not list a held release (Transfers does)").not.toContain(film.release);

    const t0 = Date.now();
    await dismiss.click();
    await expect(dismiss, "The row leaves the Transfers screen without a reload").toBeHidden({ timeout: 15_000 });
    const clear = await until("every place to clear", 15, async () => (standing(await places()).length === 0 ? true : undefined), 500).catch(() => undefined);
    expect(clear !== undefined, `Every place clears within 15 s (${clear ? `after ${((Date.now() - t0) / 1000).toFixed(1)} s` : `still in: ${standing(await places()).join(" | ")}`})`).toBe(true);
    // Dismissing answers the question; the film is still wanted and nothing was sent.
    const now = await stack.deluno.must<any>("GET", `/api/movies/${movie.id}`);
    expect(now.monitored, "The film is still wanted").toBe(true);
    await sleep(500);
  } finally {
    await page?.context().close();
    await put({ cutoffQuality: original.cutoffQuality, sizeRules: original.sizeRules ?? [] }).catch(() => undefined);
  }
});

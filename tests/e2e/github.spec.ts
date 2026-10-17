import { expect, test } from "@playwright/test";
import { DelunoApi, containerOf, docker, until, setUpStack, URLS, type Stack } from "./stack.ts";
import { openDeluno } from "./flow.ts";

/**
 * Group `github` (scripts/golden-path/suite/vm/scenarios/G13-Github.ps1, #1181): with GitHub's hourly allowance used up, the two places
 * that cannot work around the limit say so in plain words, with when it lifts in the viewer's own time:
 *   - Settings > General > the tracker definitions' "Update now" (POST /api/trackers/definitions/update-check/run; the state's error is the
 *     sentence, and the screen shows it through localizeResetTimes);
 *   - the connections' version check (GET /api/connections/versions: latestCheckPausedUntilUtc, shown beside the connection's name as
 *     "can't look for a newer version until <time> (GitHub's limit)"). A project's newest release is read from github.com's free release
 *     feed first, so the limit shows only when that route cannot be read AND the API is limited AND nothing was read before. Deluno is
 *     restarted, which empties its memory of earlier reads.
 *
 * What is real, and what is not (the stack runs with `--profile github`, tests/e2e/compose.yaml):
 *   - REAL: Deluno's own image, its own calls to api.github.com over HTTPS, and GitHub's reply: the status, headers and body recorded from
 *     the real api.github.com after the anonymous allowance was spent (tests/fixtures/recorded/github/2022-11-28/rate-limit-used-up.json).
 *   - NOT GITHUB'S: the server. The stack answers the name api.github.com itself, with a certificate from an authority it made and Deluno
 *     trusts (tests/e2e/fixture/github.mjs). The recorded reset time is long past, so the stand-in sends a reset an hour after it started
 *     (its /limit says when); nothing else in the reply is changed.
 *   - UNREACHABLE: github.com, www.github.com and codeload.github.com refuse the connection (a container with nothing listening), so the
 *     free release feed and the definitions archive cannot be read and Deluno goes to the limited API, as the scenario needs.
 * The hosted runner's own address is never used up, and no call leaves the stack.
 */
test.describe.configure({ mode: "serial" });

const GITHUB_ADMIN = `http://127.0.0.1:${process.env.E2E_GITHUB_PORT ?? 18443}`;

let stack: Stack;
let resetEpoch = 0;

/** "2026-10-09 00:35": the minute Deluno writes for a reset, in UTC (GitHubRateLimitException.UtcStamp rounds the instant up to the minute). */
const utcStamp = (epoch: number) => new Date(epoch * 1000 + 59_000).toISOString().slice(0, 16).replace("T", " ");

/** The reset as a viewer's clock reads it: hour and minute in this machine's zone, in the 12 and the 24 hour forms, the minute either side too. */
function localForms(epoch: number): string[] {
  const forms: string[] = [];
  for (const delta of [0, -1, 1]) {
    const at = new Date(epoch * 1000 + 59_000 + delta * 60_000);
    const h12 = at.getHours() % 12 === 0 ? 12 : at.getHours() % 12;
    forms.push(`${h12}:${String(at.getMinutes()).padStart(2, "0")}`, `${String(at.getHours()).padStart(2, "0")}:${String(at.getMinutes()).padStart(2, "0")}`);
  }
  return forms;
}

const hasLocalReset = (text: string, epoch: number) => localForms(epoch).some((form) => new RegExp(`(?<![0-9:])${form}(?![0-9])`).test(text));

async function standIn(): Promise<{ resetEpoch: number; calls: number; requests: string[] }> {
  return await (await fetch(`${GITHUB_ADMIN}/`)).json() as { resetEpoch: number; calls: number; requests: string[] };
}

test.beforeAll(async () => {
  test.setTimeout(10 * 60_000);
  stack = await setUpStack();
  resetEpoch = (await standIn()).resetEpoch;
});

test("github-limit.messages: With GitHub's hourly limit used up, the tracker definitions' Update now and the connections' version check name the limit, with the reset in the viewer's local time", async ({ browser }) => {
  test.setTimeout(15 * 60_000);
  expect(resetEpoch, "GitHub's stand-in says when the limit lifts").toBeGreaterThan(Date.now() / 1000);

  // ---------------------------------------------------------- the tracker definitions' Update now
  const run = await stack.deluno.call("POST", "/api/trackers/definitions/update-check/run");
  const state = run.body ?? {};
  expect.soft(run.status === 200 && state.status === "failed", `Update now (tracker definitions) answers, and says it failed (HTTP ${run.status}, status '${state.status}')`).toBe(true);
  const error1 = String(state.error ?? "");
  expect.soft(/GitHub's limit for checking releases from this network is used up until/.test(error1) && !/could not reach/.test(error1), `Update now names GitHub's limit, not an unreachable source: ${error1}`).toBe(true);
  const m = /(\d{4}-\d{2}-\d{2} \d{2}:\d{2}) UTC/.exec(error1);
  const diff = m ? Math.abs(Date.parse(`${m[1]}:00Z`) - Date.parse(`${utcStamp(resetEpoch)}:00Z`)) / 60_000 : null;
  expect.soft(diff !== null && diff <= 1, `The reset in the sentence is GitHub's own reset time, within a minute (${m ? `Deluno says ${m[1]} UTC; GitHub says ${utcStamp(resetEpoch)} UTC` : "no reset time in the sentence"})`).toBe(true);

  // ... and on the screen, the way a person presses it: Settings > General, in the viewer's own time.
  const page = await openDeluno(browser, stack, "/settings/general");
  try {
    await page.getByRole("button", { name: /^Update now$/ }).first().click({ timeout: 30_000 });
    const shown = await page.waitForFunction(() => /used up until/.test(document.body?.innerText ?? ""), undefined, { timeout: 60_000 }).then(() => true, () => false);
    const text = await page.evaluate(() => document.body?.innerText ?? "");
    const sentence = /GitHub's limit for checking releases[^\r\n]*/.exec(text)?.[0] ?? "";
    expect.soft(shown, `The screen names GitHub's limit after Update now: ${sentence || "the sentence did not appear within 60 s"}`).toBe(true);
    expect.soft(shown && !/UTC/.test(sentence) && hasLocalReset(sentence, resetEpoch), `The screen gives the reset in the viewer's local time, never as UTC: ${sentence}`).toBe(true);
  } finally { await page.context().close(); }

  // ---------------------------------------------------------- the connections' version check
  // Deluno is restarted (its memory of earlier reads is empty); github.com and codeload.github.com refuse every connection.
  docker("restart", "--time", "30", containerOf("deluno"));
  await until("Deluno to answer after its restart", 180, async () => (await fetch(`${URLS.deluno}/health`).then((r) => r.ok, () => false)) || undefined, 2000);
  await stack.deluno.dispose();
  stack.deluno = await DelunoApi.signIn();
  // Reading the versions starts each project's release read in the background; it is asked again until one says why it cannot.
  const paused = await until("a connection's newest-release read to be paused by GitHub's limit", 180, async () => {
    const rows = await stack.deluno.must<any[]>("GET", "/api/connections/versions");
    const hit = rows.filter((r) => r.latestCheckPausedUntilUtc);
    return hit.length ? hit : undefined;
  }, 5000).catch(() => undefined);
  const all = await stack.deluno.call("GET", "/api/connections/versions");
  expect.soft(!!paused, `At least one connection's newest-release read is paused by GitHub's limit (latestCheckPausedUntilUtc)${paused ? `: ${paused.value.map((r: any) => `${r.kind}/${r.id} until ${r.latestCheckPausedUntilUtc}`).join("; ")}` : `: none reported a pause in 3 minutes; ${((all.body ?? []) as any[]).map((r) => `${r.kind}=${r.version}/latest ${r.latestVersion}/problem ${r.latestCheckProblem}`).join("; ")}`}`).toBe(true);
  if (paused) {
    const until_ = Date.parse(String(paused.value[0].latestCheckPausedUntilUtc));
    expect.soft(Math.abs(until_ - resetEpoch * 1000) / 60_000 <= 1.5, `The pause lifts when GitHub's limit does, within a minute (Deluno: ${new Date(until_).toISOString()}; GitHub: ${new Date(resetEpoch * 1000).toISOString()})`).toBe(true);

    // The version sits beside the name on the screens that list connections; the stack has download clients and a processor.
    let line: string | undefined;
    for (const route of ["/indexers/download-clients", "/indexers/media-servers", "/settings/processing"]) {
      const screen = await openDeluno(browser, stack, route);
      try {
        const found = await screen.waitForFunction(() => /can't look for a newer version until/.test(document.body?.innerText ?? ""), undefined, { timeout: 20_000 }).then(() => true, () => false);
        if (found) { line = /can't look for a newer version until[^\r\n]*/.exec(await screen.evaluate(() => document.body?.innerText ?? ""))?.[0]; break; }
      } finally { await screen.context().close(); }
    }
    expect.soft(line !== undefined && /\(GitHub's limit\)/.test(line), `The connections screen says it can't look for a newer version, naming GitHub's limit: ${line ?? "no screen showed the sentence"}`).toBe(true);
    expect.soft(line !== undefined && !/UTC/.test(line) && hasLocalReset(line, resetEpoch), `It gives the reset in the viewer's local time, never as UTC: ${line}`).toBe(true);
  }
  const asked = await standIn();
  console.log(`GitHub's stand-in was asked ${asked.calls} time(s): ${asked.requests.slice(-8).join(" | ")}`);
  expect.soft(asked.calls, "Deluno's calls reached the stand-in for api.github.com (the allowance it answers for is used up)").toBeGreaterThan(0);
});

import { expect, test } from "@playwright/test";
import { CLIENT_KINDS, type ClientKind } from "./clients.ts";
import { activityRaw, handoffOf, restoreWeir, sendFilm, sleep, startWeir, stopWeir, waitImported, weirAnswers, type Sent } from "./flow.ts";
import { clientFinished, setUpStack, until, type Stack } from "./stack.ts";

/**
 * Group `problems` (scripts/golden-path/suite/vm/scenarios/G09-Problems.ps1).
 *
 * problems.dismiss-everywhere (#1228): a failed hand-off stands in Transfers (needs action), in the bell and, where Deluno raises it,
 * on System. Dismissing it in one place clears every place within seconds, with nothing to reload. Each place is read from the API
 * behind its screen.
 *
 * activity.one-story (#1226, #1230): every hand-off this run made has closing lines that tell one story: never both "imported" and
 * "will not import", no doubled full stops, one closing line for each send. On the VM the earlier groups' hand-offs are read; here the
 * group makes its own: the failed one above, and a film through every client.
 */
test.describe.configure({ mode: "serial" });

let stack: Stack;
const stories = new Map<string, Sent>();

const FAILED = ["failed", "rejected", "timed-out", "stalled", "lost", "source-gone"];

/** Where a release name shows up among the places a person looks for problems. */
async function problemPlaces(needle: string): Promise<Record<string, boolean>> {
  const all = await stack.deluno.must<any[]>("GET", "/api/integrations/processors/handoffs?take=200");
  const places: Record<string, boolean> = {
    "Transfers: needs action (hand-offs that went wrong)": all.some((h) => String(h.releaseName).includes(needle) && FAILED.includes(h.status))
  };
  for (const [name, path] of [["The bell (attention summary)", "/api/attention/summary"], ["System: health checks", "/api/system/health/summary"], ["System: health notices", "/api/system/health"]] as const) {
    const r = await stack.deluno.call("GET", path);
    places[name] = r.status === 200 && r.text.includes(needle);
  }
  return places;
}

const standing = (places: Record<string, boolean>) => Object.entries(places).filter(([, on]) => on).map(([name]) => name);

/** What is wrong with the closing lines of one film (empty when they tell one story). */
async function storyProblems(film: Sent["film"]): Promise<string[]> {
  const problems: string[] = [];
  const lines = await activityRaw(stack, film.title);
  const closing = lines.filter((l) => /Told the processor what happened|Could not tell the processor/.test(l));
  for (const c of closing.filter((l) => /imported the file/.test(l) && /will not import|will not be imported/.test(l))) problems.push(`says both imported and will not import: ${c.replace(/\s+/g, " ").slice(0, 240)}`);
  for (const c of closing.filter((l) => /[a-z0-9]\.\.(\s|"|\\|$)/.test(l))) problems.push(`ends in a doubled full stop: ${c.replace(/\s+/g, " ").slice(0, 240)}`);
  // One closing line for each send (by its own id): a resent release has the replaced send's one line and the new send's one line.
  const perSend = new Map<string, number>();
  for (const l of lines.filter((x) => /"category":"processing\.outcome-(reported|not-reported|superseded)"/.test(x))) {
    const id = /HandoffId\W+([0-9a-f]{32})/.exec(l)?.[1] ?? "unknown";
    perSend.set(id, 1 + (perSend.get(id) ?? 0));
  }
  for (const [id, n] of perSend) if (n > 1) problems.push(`${n} closing lines for the one send ${id}`);
  return problems;
}

test.beforeAll(async () => {
  test.setTimeout(15 * 60_000);
  stack = await setUpStack();
});

test.afterAll(async () => { await restoreWeir(); });

test("problems.dismiss-everywhere: A problem shown in several places clears everywhere when it is dismissed in any one", async () => {
  test.setTimeout(10 * 60_000);
  stopWeir();
  let sent: Sent;
  let failed: any;
  try {
    sent = await sendFilm(stack, "qbittorrent", "Problem", "file");
    await until("qbittorrent to finish the film", 240, () => clientFinished(stack, "qbittorrent", sent.film), 2000);
    failed = (await until("the hand-off to fail", 120, async () => {
      const h = await handoffOf(stack, sent.film.release);
      return h && ["failed", "rejected"].includes(h.status) ? h : undefined;
    }, 1000).catch(() => undefined))?.value;
  } finally { startWeir(); }
  stories.set("problem", sent!);
  expect(failed, "The hand-off failed while Weir was down (the problem exists)").toBeTruthy();

  const shown = await until("the problem to stand in at least two places", 60, async () => {
    const p = await problemPlaces(sent.film.title);
    return standing(p).length >= 2 ? p : undefined;
  }, 2000).catch(() => undefined);
  const places = shown?.value ?? await problemPlaces(sent.film.title);
  expect(standing(places).length, `The problem stands in at least two places: ${standing(places).join(" | ")}`).toBeGreaterThanOrEqual(2);

  const t0 = Date.now();
  const dismissed = await stack.deluno.call("POST", `/api/integrations/processors/handoffs/${failed.id}/dismiss`);
  expect(dismissed.status >= 200 && dismissed.status < 300, `Dismiss in Transfers is accepted (HTTP ${dismissed.status})`).toBe(true);
  const clear = await until("every place to clear", 15, async () => (standing(await problemPlaces(sent.film.title)).length === 0 ? true : undefined), 500).catch(() => undefined);
  const still = standing(await problemPlaces(sent.film.title));
  expect(clear !== undefined, `Every place clears within 15 s, with no reload (${clear ? `all clear after ${((Date.now() - t0) / 1000).toFixed(1)} s` : `still shown in: ${still.join(" | ")}`})`).toBe(true);
  await until("Weir to answer again", 120, weirAnswers, 1000);
});

test("activity.one-story: Every hand-off's closing lines in Activity tell one story", async () => {
  test.setTimeout(12 * 60_000);
  // No earlier group's hand-off exists in this run, so a film goes through every client and is followed to its closing line.
  const sends = new Map<ClientKind, Sent>();
  for (const kind of CLIENT_KINDS) sends.set(kind, await sendFilm(stack, kind, "Story", "file"));
  for (const [kind, sent] of sends) {
    stories.set(kind, sent);
    await waitImported(stack, sent);
    // The closing line is written when Deluno has told the processor what happened, a little after the import.
    await until(`the closing line of ${sent.film.release}`, 90, async () =>
      (await activityRaw(stack, sent.film.title)).some((l) => /"category":"processing\.outcome-(reported|not-reported|superseded)"/.test(l)), 2000).catch(() => undefined);
  }
  await sleep(1000);
  let checked = 0;
  const bad: string[] = [];
  for (const sent of stories.values()) {
    if (!(await handoffOf(stack, sent.film.release))) continue;
    checked++;
    const found = await storyProblems(sent.film);
    for (const p of found) bad.push(`${sent.film.release}: ${p}`);
    if (found.length) console.log(`activity lines of ${sent.film.title}:\n${(await activityRaw(stack, sent.film.title)).map((l) => l.replace(/\s+/g, " ")).join("\n")}`);
  }
  expect(checked, `At least one hand-off was checked (${checked} films with a hand-off)`).toBeGreaterThan(0);
  expect(bad.slice(0, 5), "No hand-off has a closing line that tells two stories, ends in a doubled full stop, or repeats").toEqual([]);
});

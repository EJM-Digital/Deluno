import { test } from "@playwright/test";
import { isUsenet } from "./clients.ts";
import { sendFilm, type Sent } from "./flow.ts";
import { setUpStack, type Stack } from "./stack.ts";
import { setUpTvStack, type Media } from "./tv.ts";
import { stuckKinds } from "./stuckprogresscases.ts";
import { assertStruck, readRules, restoreRules, setHealthRules, waitAllStruck, type Seen } from "./stuck.ts";

/**
 * Group `stuck`, phase B (scripts/golden-path/suite/vm/scenarios/G05-Stuck.ps1, #1206): "Too few peers" (two strikes, floor 5) and
 * "Too slow" (one strike, 2 MB/s floor) on, "No progress" off, a one-minute check. A torrent nobody has on each torrent client, and
 * a slowed Usenet release on each Usenet client (the fixture's news server answers each article only after a pause). Each is struck
 * at its own count, with the wording right ("1 check", "2 checks in a row").
 */
/** Registers the scenarios of stuck phase B ("Too few peers", "Too slow") for a media type on one stack made for the spec file. Call it at the top level of a spec. */
export function defineStuckRate(media: Media) {
  test.describe.configure({ mode: "serial" });

  const kinds = stuckKinds(media);
  const TORRENTS = kinds.filter((k) => !isUsenet(k));
  const USENET = kinds.filter(isUsenet);
  const tv = media === "tv" ? "tv." : "";

  let stack: Stack;
  let original: Awaited<ReturnType<typeof readRules>> = [];
  const sends = new Map<string, Sent>();
  const seen = new Map<string, Seen>();

  test.beforeAll(async () => {
    test.setTimeout(20 * 60_000);
    stack = media === "tv" ? await setUpTvStack() : await setUpStack();
    original = await readRules(stack);
    await setHealthRules(stack, { "low-peers": { strikes: 2, threshold: 5 }, "too-slow": { strikes: 1, threshold: 2048 } }, 1);
    for (const kind of TORRENTS) sends.set(`peers.${kind}`, await sendFilm(stack, kind, "peers", "file", { variant: "dummy" }));
    for (const kind of USENET) sends.set(`slow.${kind}`, await sendFilm(stack, kind, "slow", "folder", { slow: true }));
    for (const [key, s] of await waitAllStruck(stack, sends)) seen.set(key, s);
  });

  test.afterAll(async () => { if (stack) await restoreRules(stack, original); });

  for (const kind of TORRENTS) {
    test(`${tv}stuck.peers.${kind}: A ${kind} download with too few peers is struck at its count`, async () => {
      assertStruck(sends.get(`peers.${kind}`)!, seen.get(`peers.${kind}`), 2, /peers|seeders/i);
    });
  }
  for (const kind of USENET) {
    test(`${tv}stuck.slow.${kind}: A ${kind} download that is too slow is struck at its count`, async () => {
      assertStruck(sends.get(`slow.${kind}`)!, seen.get(`slow.${kind}`), 1, /slow|speed|stayed under/i, { inARow: false });
    });
  }
}

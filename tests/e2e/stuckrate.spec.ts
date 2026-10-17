import { test } from "@playwright/test";
import { CLIENT_KINDS, isUsenet } from "./clients.ts";
import { sendFilm, type Sent } from "./flow.ts";
import { setUpStack, type Stack } from "./stack.ts";
import { assertStruck, readRules, restoreRules, setHealthRules, waitAllStruck, type Seen } from "./stuck.ts";

/**
 * Group `stuck`, phase B (scripts/golden-path/suite/vm/scenarios/G05-Stuck.ps1, #1206): "Too few peers" (two strikes, floor 5) and
 * "Too slow" (one strike, 2 MB/s floor) on, "No progress" off, a one-minute check. A torrent nobody has on each torrent client, and
 * a slowed Usenet release on each Usenet client (the fixture's news server answers each article only after a pause). Each is struck
 * at its own count, with the wording right ("1 check", "2 checks in a row").
 */
test.describe.configure({ mode: "serial" });

const TORRENTS = CLIENT_KINDS.filter((k) => !isUsenet(k));
const USENET = CLIENT_KINDS.filter(isUsenet);

let stack: Stack;
let original: Awaited<ReturnType<typeof readRules>> = [];
const sends = new Map<string, Sent>();
const seen = new Map<string, Seen>();

test.beforeAll(async () => {
  test.setTimeout(20 * 60_000);
  stack = await setUpStack();
  original = await readRules(stack);
  await setHealthRules(stack, { "low-peers": { strikes: 2, threshold: 5 }, "too-slow": { strikes: 1, threshold: 2048 } }, 1);
  for (const kind of TORRENTS) sends.set(`peers.${kind}`, await sendFilm(stack, kind, "peers", "file", { variant: "dummy" }));
  for (const kind of USENET) sends.set(`slow.${kind}`, await sendFilm(stack, kind, "slow", "folder", { slow: true }));
  for (const [key, s] of await waitAllStruck(stack, sends)) seen.set(key, s);
});

test.afterAll(async () => { if (stack) await restoreRules(stack, original); });

for (const kind of TORRENTS) {
  test(`stuck.peers.${kind}: A ${kind} download with too few peers is struck at its count`, async () => {
    assertStruck(sends.get(`peers.${kind}`)!, seen.get(`peers.${kind}`), 2, /peers|seeders/i);
  });
}
for (const kind of USENET) {
  test(`stuck.slow.${kind}: A ${kind} download that is too slow is struck at its count`, async () => {
    assertStruck(sends.get(`slow.${kind}`)!, seen.get(`slow.${kind}`), 1, /slow|speed|stayed under/i, { inARow: false });
  });
}

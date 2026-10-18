import { test } from "@playwright/test";
import { CLIENT_KINDS, isUsenet, type ClientKind } from "./clients.ts";
import { sendFilm, type Sent } from "./flow.ts";
import { setUpStack, type Stack } from "./stack.ts";
import { setUpTvStack, type Media } from "./tv.ts";
import { assertStruck, readRules, restoreRules, setHealthRules, waitAllStruck, type Seen } from "./stuck.ts";

/**
 * Group `stuck`, phase A (scripts/golden-path/suite/vm/scenarios/G05-Stuck.ps1, #1206, #1225): "No progress" on, two strikes, a
 * one-minute check. A torrent with no data and no peers and a torrent missing its last piece (every torrent client), and a Usenet
 * release that is never answered and one whose last article is never answered (every Usenet client). Each is struck at its count,
 * with the plural wording right, replaced, and a download stuck just short of the end never reads 100% or "Not handed over".
 *
 * How each is made with no internet: the "no data" torrent has no web seed and no tracker, so nobody has it; the "short" torrent's
 * web seed (the fixture) stops before the last piece and leaves the request open; the Usenet releases are posted to the fixture's
 * news server, which never answers every article of one, or the last article of the other. A Usenet client works down its queue, so
 * the release stuck just short of the end goes first and the one stuck at 0 bytes is sent once that one is struck and gone.
 */
/** The clients a media type runs: the movies run all five; a TV episode runs one torrent and one Usenet client. */
export const stuckKinds = (media: Media): ClientKind[] => (media === "tv" ? ["qbittorrent", "sabnzbd"] : CLIENT_KINDS);

/** Registers the scenarios of stuck phase A ("No progress") for a media type on one stack made for the spec file. Call it at the top level of a spec. */
export function defineStuckProgress(media: Media) {
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
    await setHealthRules(stack, { "no-progress": { strikes: 2 } }, 1);
    const first = new Map<string, Sent>();
    for (const kind of TORRENTS) {
      first.set(`zero.${kind}`, await sendFilm(stack, kind, "zero", "file", { variant: "dummy" }));
      first.set(`short.${kind}`, await sendFilm(stack, kind, "short", "file", { variant: "short" }));
    }
    for (const kind of USENET) first.set(`short.${kind}`, await sendFilm(stack, kind, "short", "folder", { hang: "last" }));
    for (const [key, sent] of first) sends.set(key, sent);
    for (const [key, s] of await waitAllStruck(stack, first)) seen.set(key, s);

    const zero = new Map<string, Sent>();
    for (const kind of USENET) zero.set(`zero.${kind}`, await sendFilm(stack, kind, "zero", "folder", { hang: "all" }));
    for (const [key, sent] of zero) sends.set(key, sent);
    for (const [key, s] of await waitAllStruck(stack, zero)) seen.set(key, s);
  });

  test.afterAll(async () => { if (stack) await restoreRules(stack, original); });

  for (const kind of kinds) {
    test(`${tv}stuck.zero.${kind}: A ${kind} download stuck at 0 bytes is struck at its count`, async () => {
      assertStruck(sends.get(`zero.${kind}`)!, seen.get(`zero.${kind}`), 2, /No progress/i);
    });
  }
  for (const kind of kinds) {
    test(`${tv}stuck.short.${kind}: A ${kind} download stuck just short of the end is struck at its count`, async () => {
      assertStruck(sends.get(`short.${kind}`)!, seen.get(`short.${kind}`), 2, /No progress/i, { shortOfEnd: true });
    });
  }
}

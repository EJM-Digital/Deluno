import { expect, test } from "@playwright/test";
import { CLIENT_KINDS, type ClientKind } from "./clients.ts";
import { handoffOf, sendFilm, sleep, type Sent } from "./flow.ts";
import { assertResendSettled, handoffsOf, removeDownload, setWeirPause, startAgainDownload } from "./resend.ts";
import { clientFinished, setUpStack, until, type Stack } from "./stack.ts";

/**
 * Group `resend`, timing "before" (scripts/golden-path/suite/vm/scenarios/G04-Resend.ps1): "I don't want it", then a resend of
 * the same release to the same client, while Weir has not started the first send (Weir is paused, through its own API). Every
 * client runs it, all in one window as on the VM. The new hand-off must settle on its own with one import, the first send's late
 * report must be quiet, and nothing may be left unmatched in Weir's output (#1208, #1226, #1242).
 */
test.describe.configure({ mode: "serial" });

let stack: Stack;
const sends = new Map<ClientKind, Sent>();
const firsts = new Map<ClientKind, any>();

test.beforeAll(async () => {
  test.setTimeout(15 * 60_000);
  stack = await setUpStack();
  try {
    await setWeirPause(true);
    for (const kind of CLIENT_KINDS) sends.set(kind, await sendFilm(stack, kind, "rebefore", "file"));
    for (const kind of CLIENT_KINDS) {
      const { film } = sends.get(kind)!;
      await until(`${kind} to finish ${film.release}`, 240, () => clientFinished(stack, kind, film), 1000);
      const first = await until(`the first hand-off of ${film.release}`, 90, () => handoffOf(stack, film.release), 1000).catch(() => undefined);
      if (first) firsts.set(kind, first.value);
    }
    for (const kind of CLIENT_KINDS) {
      const sent = sends.get(kind)!;
      if (!firsts.get(kind)) continue;
      await removeDownload(stack, sent, "unwanted");
      await sleep(1000);
      try { await startAgainDownload(stack, sent); } catch (error) { console.log(`${sent.film.release}: ${String(error).slice(0, 300)}`); }
    }
    // Every client has its second hand-off before Weir is resumed.
    await until("every client to have its second hand-off", 120, async () => {
      for (const kind of CLIENT_KINDS) {
        const first = firsts.get(kind);
        if (!first) continue;
        if (!(await handoffsOf(stack, sends.get(kind)!.film.release)).some((h) => h.id !== first.id)) return false;
      }
      return true;
    }, 1000).catch(() => undefined);
  } finally { try { await setWeirPause(false); } catch (error) { console.log(`could not resume Weir: ${error}`); } }
});

test.afterAll(async () => { try { await setWeirPause(false); } catch { /* the next stack starts clean */ } });

for (const kind of CLIENT_KINDS) {
  test(`resend.before.${kind}: I don't want it, then a resend to ${kind}, before Weir starts the first send`, async () => {
    test.setTimeout(10 * 60_000);
    const first = firsts.get(kind);
    expect(first, `The first hand-off for ${kind} appeared`).toBeTruthy();
    await assertResendSettled(stack, sends.get(kind)!, first, `before/${kind}`);
  });
}

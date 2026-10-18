import { expect, test } from "@playwright/test";
import { CLIENT_KINDS, type ClientKind } from "./clients.ts";
import {
  drawerText, handoffOf, importedFile, openDeluno, restoreWeir, sendFilm, weirFile, sleep, startWeir, stopWeir, weirAnswers,
  type Sent
} from "./flow.ts";
import { clientFinished, setUpStack, until, type Stack } from "./stack.ts";
import { setUpTvStack, type Media } from "./tv.ts";

/**
 * Group `tryagain` (scripts/golden-path/suite/vm/scenarios/G07-TryAgain.ps1, #1204, #1229): with Weir down, a film's hand-off
 * fails; Weir comes back; the drawer's "Send to Weir ... again" is pressed and the drawer is read every 200 ms until the film is
 * imported. At no step may the old failure show again, and the drawer's header must follow the real hand-off. Every client's
 * film goes through it; the five hand-offs fail in one outage. Weir is stopped and started as a container; the drawer is
 * read in a real browser; the client's own API says when its download finished.
 */
/** The clients a media type runs: the movies run all five; a TV episode runs one torrent and one Usenet client. */
export const tryAgainKinds = (media: Media): ClientKind[] => (media === "tv" ? ["qbittorrent", "sabnzbd"] : CLIENT_KINDS);

/** Registers the Try again scenarios of a media type on one stack made for the spec file. Call it at the top level of a spec. */
export function defineTryAgain(media: Media) {
  test.describe.configure({ mode: "serial" });
  const kinds = tryAgainKinds(media);
  const tv = media === "tv" ? "tv." : "";

  const OLD_FAILURE = /Needs your action|hand-?off failed|Could not send to|Rejected Action|could not submit/i;

  let stack: Stack;
  const films = new Map<ClientKind, Sent>();
  const failed = new Map<ClientKind, any>();
  let weirBack = false;

  test.beforeAll(async () => {
    test.setTimeout(15 * 60_000);
    stack = media === "tv" ? await setUpTvStack() : await setUpStack();
    stopWeir();
    try {
      for (const kind of kinds) films.set(kind, await sendFilm(stack, kind, "Retry", "file"));
      for (const kind of kinds) {
        const { film } = films.get(kind)!;
        await until(`${kind} to finish ${film.release}`, 240, () => clientFinished(stack, kind, film), 2000);
        try {
          const h = await until(`the hand-off of ${film.release} to fail`, 120, async () => {
            const found = await handoffOf(stack, film.release);
            return found && ["failed", "rejected"].includes(found.status) ? found : undefined;
          }, 1000);
          failed.set(kind, h.value);
        } catch (error) { console.log(`${kind}: ${error}`); }
      }
    } finally { startWeir(); }
    try { await until("Weir to answer again", 120, weirAnswers, 1000); weirBack = true; } catch { /* the first check says so */ }
    // A person would not press the instant Weir is back: Deluno's own check interval is the behaviour being allowed for.
    await sleep(20_000);
  });

  test.afterAll(async () => { await restoreWeir(); });

  for (const kind of kinds) {
    test(`${tv}tryagain.${kind}: Try again on a failed ${kind} hand-off: no old failure at any step, the drawer follows the hand-off`, async ({ browser }) => {
      test.setTimeout(10 * 60_000);
      const { film } = films.get(kind)!;
      expect(weirBack, "Weir is answering again").toBe(true);
      const h = failed.get(kind);
      expect(h, `The hand-off failed while Weir was down${h ? ` (status ${h.status}: ${h.failureMessage})` : " (it never failed)"}`).toBeTruthy();

      const page = await openDeluno(browser, stack, "/queue");
      try {
        // The download opens in Transfers.
        await page.getByRole("row").filter({ hasText: film.title }).first().click({ timeout: 30_000 });
        const again = page.getByRole("button", { name: /^Send to .* again/ });
        await expect(again, 'The drawer offers "Send to Weir ... again"').toBeVisible({ timeout: 20_000 }).catch(async (error) => {
          const now = await handoffOf(stack, film.release);
          throw new Error(`${error.message}\npage: ${page.url()}\nhand-off now: ${now ? `${now.status} / ${now.failureMessage}` : "none"}\ndrawer or page text:\n${await drawerText(page)}`);
        });
        expect(await drawerText(page), "Before the press the drawer shows the failure").toMatch(OLD_FAILURE);

        const pressedAt = Date.now();
        await again.click();
        const samples: Array<{ at: number; text: string }> = [];
        let serverMoved = 0, headerMoved = 0, sawSending = 0, imported = false;
        while (Date.now() - pressedAt < 180_000) {
          const text = await drawerText(page).catch(() => "");
          const now = Date.now();
          samples.push({ at: now, text: text.replace(/\s+/g, " ") });
          const cur = await handoffOf(stack, film.release);
          // "waiting" is Deluno's own queue for the send (the retry goes in its next pass), and the header says Sending again for
          // exactly that: the hand-off has moved on once it is submitted.
          if (!serverMoved && cur && !["failed", "rejected", "waiting"].includes(cur.status)) serverMoved = now;
          if (!sawSending && /Sending to .* again/.test(text)) sawSending = now;
          if (serverMoved && !headerMoved && !/Sending to .* again/.test(text)) headerMoved = now;
          if (cur?.status === "completed" && cur.importJobId && await importedFile(film.title)) { imported = true; await sleep(2000); break; }
          await sleep(200);
        }
        expect(imported, "The film was imported after Try again").toBe(true);
        const bad = samples.filter((s) => OLD_FAILURE.test(s.text));
        expect(bad.length, `No old failure showed at any step after the press (${samples.length} samples every ~200 ms; ${bad.length} showed it${bad.length ? `, first at +${((bad[0].at - pressedAt) / 1000).toFixed(1)} s: ${bad[0].text.slice(0, 160)}` : ""})`).toBe(0);
        expect(sawSending > 0 && (sawSending - pressedAt) / 1000 <= 3, `The header said "Sending to Weir ... again" right after the press (${sawSending ? `at +${((sawSending - pressedAt) / 1000).toFixed(1)} s` : "never"})`).toBe(true);
        if (serverMoved) {
          const lag = headerMoved ? (headerMoved - serverMoved) / 1000 : 999;
          expect(lag, `The drawer follows the real hand-off: its header moves on within 10 s of Deluno submitting (submitted at +${((serverMoved - pressedAt) / 1000).toFixed(1)} s)`).toBeLessThanOrEqual(10);
        }
        const weirSays = await weirFile(film.title);
        expect(weirSays?.status, "Weir's own record of the file says it processed it").toBe("processed");
        const steps = [...new Set(samples.flatMap((s) => { const m = /(Sending to [^.]{0,40} again|Waiting for processor|Import queued|Imported|Cleaning|Processing)/.exec(s.text); return m ? [m[1]] : []; }))];
        console.log(`${tv}tryagain.${kind}: steps read in the drawer: ${steps.join(" > ")}`);
      } finally { await page.context().close(); }
    });
  }
}

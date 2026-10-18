import { expect, test } from "@playwright/test";
import { CLIENT_KINDS, type ClientKind } from "./clients.ts";
import { setUpTvStack, type Media } from "./tv.ts";
import {
  activityLines, deleteCompletedFiles, handoffOf, importedFile, restoreWeir, sendFilm, setAutomation, sleep, telemetryRows, type Sent
} from "./flow.ts";
import { setWeirPause } from "./resend.ts";
import { clientFinished, filesUnder, setUpStack, until, type Stack } from "./stack.ts";

/**
 * Group `vanish` (scripts/golden-path/suite/vm/scenarios/G08-Vanish.ps1, #1210, #1186): the files of a finished download are
 * deleted. Before the hand-off (automation is paused so a finished download waits), Deluno must say "Files are gone" within
 * seconds and never hand it off. While the hand-off waits at Weir (Weir is paused, through its own API), Deluno must say so too,
 * and the hand-off must end in "source gone" once Weir reports it, with nothing imported and nothing left in Weir's output.
 * Every client runs both. The two timings are two spec files, so each stays inside its own shard's time.
 */
export type Phase = "before" | "waiting";

const SOURCE_GONE_WAIT_MIN = 14; // Weir waits for a file that is not there before it reports the source gone (about 11 minutes in rc.13)

/** The row that says "Files are gone" for the film, if Deluno has one yet. */
async function filesGoneRow(stack: Stack, sent: Sent) {
  return (await telemetryRows(stack, true)).find((row) =>
    (String(row.releaseName ?? "").includes(sent.film.release) || String(row.title ?? "").includes(sent.film.title)) && row.status === "filesGone");
}

/** The clients a media type runs: the movies run all five; a TV episode runs one torrent and one Usenet client. */
export const vanishKinds = (media: Media): ClientKind[] => (media === "tv" ? ["qbittorrent", "sabnzbd"] : CLIENT_KINDS);

export function defineVanish(phase: Phase, media: Media = "movies") {
  const kinds = vanishKinds(media);
  const tv = media === "tv" ? "tv." : "";
  test.describe.configure({ mode: "serial" });
  let stack: Stack;
  const films = new Map<ClientKind, Sent>();
  const flaggedAfter = new Map<ClientKind, number>();
  const goneAt = new Map<ClientKind, number>();
  const label = phase === "before" ? "gone" : "gonewait";

  test.beforeAll(async () => {
    test.setTimeout(25 * 60_000);
    stack = media === "tv" ? await setUpTvStack() : await setUpStack();
    const removedAt = new Map<ClientKind, number>();
    try {
      if (phase === "before") await setAutomation(stack, false); else await setWeirPause(true);
      for (const kind of kinds) films.set(kind, await sendFilm(stack, kind, label, "file"));
      for (const kind of kinds) {
        const { film } = films.get(kind)!;
        await until(`${kind} to finish ${film.release}`, 240, () => clientFinished(stack, kind, film), 1000);
        if (phase === "waiting") await until(`the hand-off of ${film.release}`, 90, () => handoffOf(stack, film.release), 1000);
      }
      for (const kind of kinds) {
        const n = await deleteCompletedFiles(films.get(kind)!.film.title);
        removedAt.set(kind, Date.now());
        console.log(`   ${kind}: deleted ${n} files`);
      }
      // While automation (or Weir) is still held, Deluno must say so itself within one check.
      const deadline = Date.now() + 90_000;
      while (Date.now() < deadline && flaggedAfter.size < kinds.length) {
        for (const kind of kinds) {
          if (!flaggedAfter.has(kind) && await filesGoneRow(stack, films.get(kind)!).catch(() => undefined)) flaggedAfter.set(kind, Math.round((Date.now() - removedAt.get(kind)!) / 100) / 10);
        }
        await sleep(500);
      }
    } finally {
      if (phase === "before") await setAutomation(stack, true).catch((e) => console.log(`could not resume automation: ${e}`));
      else await setWeirPause(false).catch((e) => console.log(`could not resume Weir: ${e}`));
      await restoreWeir().catch(() => undefined);
    }
    if (phase === "before") {
      // Automation is back on: give it time to (wrongly) hand the film on, so "never handed off" is read after it has had its chance.
      await sleep(45_000);
    } else {
      const deadline = Date.now() + SOURCE_GONE_WAIT_MIN * 60_000;
      while (Date.now() < deadline && goneAt.size < kinds.length) {
        for (const kind of kinds) {
          if (goneAt.has(kind)) continue;
          const h = await handoffOf(stack, films.get(kind)!.film.release).catch(() => undefined);
          if (h && (h.status === "source-gone" || (h.status === "dismissed" && /gone|no longer/.test(String(h.failureMessage ?? ""))))) goneAt.set(kind, Date.now());
        }
        await sleep(5000);
      }
    }
  });

  for (const kind of kinds) {
    if (phase === "before") {
      test(`${tv}vanish.before.${kind}: A ${kind} download's files vanish before the hand-off: Files are gone, never handed off`, async () => {
        const sent = films.get(kind)!;
        const seen = flaggedAfter.get(kind);
        console.log(`vanish.${phase}.${kind}: Files are gone shown ${seen} s after the delete`);
        expect.soft(seen !== undefined && seen <= 30, `Deluno said "Files are gone" within 30 s (${seen !== undefined ? `after ${seen} s` : "it never did"})`).toBe(true);
        const h = await handoffOf(stack, sent.film.release);
        expect.soft(h, `It was never handed to Weir (not after automation resumed either)${h ? `: a hand-off exists: ${h.status}` : ""}`).toBeUndefined();
        expect.soft(await importedFile(sent.film.title), "Nothing of it was imported").toBeUndefined();
      });
    } else {
      test(`${tv}vanish.waiting.${kind}: A ${kind} download's files vanish while the hand-off waits: it ends as source gone, nothing imported`, async () => {
        const sent = films.get(kind)!;
        const h = await handoffOf(stack, sent.film.release);
        expect(h, "There was a hand-off").toBeTruthy();
        const seen = flaggedAfter.get(kind);
        console.log(`vanish.${phase}.${kind}: Files are gone shown ${seen} s after the delete`);
        expect.soft(seen !== undefined && seen <= 30, `Deluno said "Files are gone" within 30 s, while Weir was still paused (${seen !== undefined ? `after ${seen} s` : "it never did"})`).toBe(true);
        expect.soft(goneAt.has(kind), `The hand-off ends as "source gone" once Weir reports it (a plain reason, a new copy offered): status ${h.status}: ${h.failureMessage} (watched up to ${SOURCE_GONE_WAIT_MIN} min after Weir resumes)`).toBe(true);
        expect.soft(await importedFile(sent.film.title), "Nothing of it was imported").toBeUndefined();
        const waiting = await filesUnder("weir", sent.film.title);
        expect.soft(waiting.map((f) => f.path), "Nothing of it waits in Weir's output").toEqual([]);
        const said = (await activityLines(stack, sent.film.title)).filter((l) => /gone|no longer/.test(l));
        expect.soft(said.length, "Activity says in plain words that the files are gone").toBeGreaterThanOrEqual(1);
      });
    }
  }
}

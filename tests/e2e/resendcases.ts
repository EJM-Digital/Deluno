import { expect, test } from "@playwright/test";
import { isUsenet, type ClientKind } from "./clients.ts";
import fs from "node:fs";
import path from "node:path";
import { activityLines } from "./flow.ts";
import {
  assertResendSettled, handoffsOf, openFirstSend, removeDownload, startAgainDownload, wasFirstImported, weirIntakeStatus, type Timing
} from "./resend.ts";
import { FOLDERS, setUpStack, until, type Stack } from "./stack.ts";
import { setUpTvStack, type Media } from "./tv.ts";

/**
 * The scenarios of group `resend` for the timings "during" and "after" (scripts/golden-path/suite/vm/scenarios/G04-Resend.ps1),
 * so a spec file can run a share of the clients: one stack per spec file, one client at a time on it. "I don't want it", then a
 * resend of the same release to the same client, while Weir is working on the first send (during) or after it has finished it
 * but Deluno has not queued its import (after). The first send is held at Weir (paused, through its own API) until Weir is let
 * go, so the moment is made, not waited for.
 */

/** Registers one scenario per client for a timing, on one stack made for the file. Call it at the top level of a spec. */
export function defineResend(timing: Timing, kinds: ClientKind[], extra?: (stack: () => Stack) => void, media: Media = "movies") {
  test.describe.configure({ mode: "serial" });
  const tv = media === "tv" ? "tv." : "";
  let stack: Stack;
  test.beforeAll(async () => { test.setTimeout(10 * 60_000); stack = media === "tv" ? await setUpTvStack() : await setUpStack(); });

  for (const kind of kinds) {
    test(`${tv}resend.${timing}.${kind}: I don't want it, then a resend to ${kind}, while Weir is ${timing === "during" ? "working on" : "done with"} the first send`, async () => {
      test.setTimeout(15 * 60_000);
      const notes: string[] = [];
      const moment = await openFirstSend(stack, kind, timing, `re${timing}`, notes);
      console.log(notes.join("\n"));
      expect(moment.reached, `The ${timing} moment was reached with the first send still open (${moment.first ? `processor '${moment.first.processor?.state}', status '${moment.first.status}'` : `missed three times (${moment.what})`})`).toBe(true);
      await removeDownload(stack, moment.sent, "unwanted");
      // Whether the first send reached the library before the removal (Deluno imports a cleaned film within a second or two).
      const firstWasImported = await wasFirstImported(moment.sent);
      if (firstWasImported) console.log(`${timing}/${kind}: the first send was imported before the removal landed.`);
      await startAgainDownload(stack, moment.sent);
      await assertResendSettled(stack, moment.sent, moment.first, `${timing}/${kind}`, {
        firstWasImported, sameHashMayCountAsImported: timing === "after" && !isUsenet(kind)
      });
    });
  }
  extra?.(() => stack);
}

/**
 * The race seen on the VM: no second hand-off appears. Keeps every hand-off row of the film (with updatedUtc), its Activity lines
 * and the client's queue row, in the log and in artifacts/e2e/evidence, so the cause can be read from the evidence.
 */
async function captureTakeoverEvidence(s: Stack, sent: Sent, when: string) {
  const handoffs = await handoffsOf(s, sent.film.release).catch((e) => `unreadable: ${e}`);
  const activity = await activityLines(s, sent.film.title).catch((e) => [`unreadable: ${e}`]);
  const telemetry = await s.deluno.must<any>("GET", "/api/download-clients/telemetry").catch(() => undefined);
  const queue = (telemetry?.clients ?? []).flatMap((c: any) => (c.queue ?? []).filter(Boolean))
    .filter((r: any) => String(r.releaseName ?? "").includes(sent.film.release) || String(r.title ?? "").includes(sent.film.title));
  const text = JSON.stringify({ when, at: new Date().toISOString(), handoffs, queue, activity }, null, 2);
  console.log(`takeover evidence (${when}): no second hand-off appeared.\n${text}`);
  const dir = path.resolve(process.cwd(), "artifacts/e2e/evidence");
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, `takeover-${when}.json`), text);
}

/** `resend.single-file.takeover` (#1262): a single file sent again while Weir works on it. */
export function defineTakeover(stack: () => Stack) {
  test("resend.single-file.takeover: A single file is sent again while Weir works on it: Deluno imports from the output path Weir names, or finds the cleaned file itself when Weir names none", async () => {
    test.setTimeout(15 * 60_000);
    const s = stack();
    const notes: string[] = [];
    const moment = await openFirstSend(s, "qbittorrent", "during", "retakeover", notes);
    console.log(notes.join("\n"));
    expect(moment.reached, `The during moment was reached with the first send still open (${moment.first ? `processor '${moment.first.processor?.state}', status '${moment.first.status}'` : `missed three times (${moment.what})`})`).toBe(true);
    const { sent, first } = moment;
    await removeDownload(s, sent, "unwanted");
    const firstWasImported = await wasFirstImported(sent);
    if (firstWasImported) console.log("takeover/qbittorrent: the first send was imported before the removal landed.");
    await startAgainDownload(s, sent);

    // What Weir itself says of the resend, read as soon as it is completed (before Deluno reports its outcome and Weir lets the hand-off go).
    expect(s.weirSecret, "The secret Deluno shares with Weir is known to the suite (the stack was made by this run)").toBeTruthy();
    const weir = await until("Weir's final answer for the resend", 180, async () => {
      const second = (await handoffsOf(s, sent.film.release)).find((h) => h.id !== first.id);
      if (!second) return undefined;
      const status = await weirIntakeStatus(s, second.id);
      return status && ["completed", "cancelled", "failed"].includes(status.state) ? { id: second.id, status } : undefined;
    }, 500).catch(() => undefined);
    if ((await handoffsOf(s, sent.film.release)).every((h) => h.id === first.id)) await captureTakeoverEvidence(s, sent, "after-weir-wait");
    const answer = weir?.value.status;
    expect.soft(answer?.state, `Weir's final answer for the resend is 'completed' (${answer ? `state '${answer.state}', outputPath '${answer.outputPath}'` : "Weir gave no final answer for the resend in 180 s"})`).toBe("completed");
    // Either contract is right. Weir before 1.0.0-rc.18 names no path in a resend's finished answer; from rc.18 it does.
    const weirNamesPath = !!answer && String(answer.outputPath ?? "").trim() !== "";
    console.log(`takeover/qbittorrent: Weir's finished answer for the resend ${weirNamesPath ? `names the output path '${answer.outputPath}'; the branch run is: Deluno imports from the path Weir names` : "names no output path; the branch run is: Deluno finds the cleaned file through the layout fallback and says so"}.`);

    await assertResendSettled(s, sent, first, "takeover/qbittorrent", { firstWasImported });

    const second = (await handoffsOf(s, sent.film.release)).find((h) => h.id !== first.id);
    if (!second) await captureTakeoverEvidence(s, sent, "after-settle");
    const lib = (await s.deluno.must<any[]>("GET", "/api/libraries")).find((l) => l.mediaType === "movies");
    const watched = String(lib?.downloadsPath || FOLDERS.moviesDownloads);
    const output = String(lib?.processorOutputPath || FOLDERS.weirMoviesOutput);
    if (weirNamesPath) {
      // Weir names the path: Deluno imports from exactly that path.
      const named = String(answer.outputPath).replace(/\/+$/, "");
      const used = second ? String(second.outputPath).replace(/\/+$/, "") : "";
      expect.soft(!!second && used.toLowerCase() === named.toLowerCase(), `Deluno imported the file from the output path Weir named (${second ? `Weir named '${named}', hand-off output '${used}'` : "there is no second hand-off"})`).toBe(true);
    } else {
      // Weir names none: Deluno found the cleaned file where Weir puts it, the source's path below the watched folder, under the output folder.
      const source = second ? String(second.sourcePath) : "";
      const w = watched.replace(/\/+$/, "");
      const expected = source.toLowerCase().startsWith(`${w.toLowerCase()}/`) ? `${output.replace(/\/+$/, "")}/${source.slice(w.length + 1)}` : undefined;
      expect.soft(!!expected && !!second && String(second.outputPath).toLowerCase() === expected.toLowerCase(), `Deluno imported the file from <output folder>/<source path relative to the watched folder> (${second ? `source '${source}', expected '${expected}', hand-off output '${second.outputPath}'` : "there is no second hand-off"})`).toBe(true);
      // Deluno's own record of what it did (its Activity feed): it names the cleaned file it found without being told where it was.
      const fallback = (await activityLines(s, sent.film.title)).filter((l) => /found the cleaned file|matched processed output|found the processed file/.test(l));
      expect.soft(fallback.length, `Deluno's log names that it found the cleaned file itself (${fallback.length} Activity rows)`).toBeGreaterThan(0);
    }
  });
}

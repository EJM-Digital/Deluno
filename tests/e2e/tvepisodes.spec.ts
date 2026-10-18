import { expect, test } from "@playwright/test";
import { CLIENT_KINDS, type ClientKind } from "./clients.ts";
import { activityLines, handoffOf, sendFilm, type Sent } from "./flow.ts";
import { clientFinished, filesUnder, settledLibraryFile, until, type Stack } from "./stack.ts";
import { TV_FOLDERS, setUpTvStack } from "./tv.ts";

/**
 * Group `tvepisodes` (the show-detail rows E2.14.1 and E2.14.2 of docs/testing/coverage-register.json): a real single TV episode of a
 * made-up show, searched and grabbed through Deluno ("Search" on the episode, which finds the release on the feed), sent to each of
 * the five clients, handed to Weir (the TV library's folders), cleaned and imported into the TV library as that episode. These are the
 * checks of `film.<client>` (tests/e2e/films.spec.ts), read the same way: the client's own API says it finished, Deluno handed it to
 * Weir, the hand-off settled, the cleaned file is in the library, and Activity says sent, handed over and imported. The show's episodes
 * come from the fixture acting as the metadata broker (tests/e2e/tv.ts). All five are searched first so the clients and Weir work at once.
 */
test.describe.configure({ mode: "serial" });

interface Outcome {
  finished: boolean; finishedSec: number; handedOff: boolean; handedSec: number;
  settled: boolean; imported: boolean; importedSec: number; handoff: any; file?: { path: string; size: number };
}
interface Run { kind: ClientKind; sent: Sent; outcome: Outcome }

let stack: Stack;
const runs = new Map<ClientKind, Run>();

const secondsSince = (run: Run) => Math.round((Date.now() - run.sent.sentAt) / 100) / 10;

async function watch(run: Run) {
  const { sent, kind, outcome } = run;
  if (!outcome.finished && await clientFinished(stack, kind, sent.film)) { outcome.finished = true; outcome.finishedSec = secondsSince(run); }
  const h = await handoffOf(stack, sent.film.release);
  if (h && !outcome.handedOff) { outcome.handedOff = true; outcome.handedSec = secondsSince(run); }
  outcome.handoff = h;
  outcome.settled = !!h && h.status === "completed";
  const file = await settledLibraryFile(stack, sent.film.title);
  if (file && !outcome.imported) { outcome.imported = true; outcome.importedSec = secondsSince(run); outcome.file = file; }
  return outcome.imported && outcome.settled;
}

test.beforeAll(async () => {
  test.setTimeout(15 * 60_000);
  stack = await setUpTvStack();
  for (const kind of CLIENT_KINDS) {
    const sent = await sendFilm(stack, kind, "episode", "folder");
    runs.set(kind, { kind, sent, outcome: { finished: false, finishedSec: 0, handedOff: false, handedSec: 0, settled: false, imported: false, importedSec: 0, handoff: null } });
  }
  try {
    await until("every episode to be imported", 8 * 60, async () => {
      let all = true;
      for (const run of runs.values()) if (!await watch(run)) all = false;
      return all;
    }, 2000);
  } catch (error) {
    console.log(`An episode did not finish: ${error}`);
    for (const run of runs.values()) console.log(`  ${run.kind}: ${JSON.stringify({ ...run.outcome, handoff: undefined })}; hand-off ${JSON.stringify(run.outcome.handoff)?.slice(0, 600)}`);
  }
});

for (const kind of CLIENT_KINDS) {
  test(`tv.episode.${kind}: a TV episode searched and grabbed through ${kind} is downloaded, handed to Weir and imported as that episode`, async () => {
    const { sent, outcome } = runs.get(kind)!;
    const { film } = sent;
    expect(sent.search.queuedCount, `Searching the episode found its release and sent it (outcome ${sent.search.outcome}, release ${sent.search.releaseName ?? film.release})`).toBeGreaterThan(0);
    expect(outcome.finished, "The client finished the download").toBe(true);
    expect(outcome.handedOff, "Deluno handed it to Weir").toBe(true);
    expect(outcome.settled, `Weir cleaned it and the hand-off settled (status ${outcome.handoff?.status}, processor said ${outcome.handoff?.processor?.state})`).toBe(true);
    expect(outcome.imported, "Deluno imported the cleaned file into the TV library").toBe(true);
    expect(outcome.file?.path ?? "", "The file is under the TV library's folder").toContain(TV_FOLDERS.library.replace("/media", ""));
    expect(outcome.file!.size).toBeGreaterThan(1_000_000);
    expect(String(outcome.handoff?.outputPath ?? ""), "Weir's output is in its TV output folder").toContain(TV_FOLDERS.weirOutput.replace("/weir", ""));

    // The episode itself is now owned: Deluno's inventory says S01E01 has its file, and no other episode of the show does.
    const inventory = await stack.deluno.must<any>("GET", `/api/series/${sent.seriesId}/inventory`);
    const withFile = ((inventory.episodes ?? []) as any[]).filter((e) => e.hasFile).map((e) => `S${String(e.seasonNumber).padStart(2, "0")}E${String(e.episodeNumber).padStart(2, "0")}`);
    expect(withFile, "The show owns exactly the episode that was searched for").toEqual(["S01E01"]);
    expect((await filesUnder("media", film.title)).filter((f) => /[.](mkv|mp4|avi)$/i.test(f.path)).length, "One video file is in the library").toBe(1);

    const lines = await activityLines(stack, film.title);
    for (const said of ["Sent to the download client", "Sent to the processor", "Imported into the library"]) {
      expect(lines.some((m) => m.includes(said.toLowerCase())), `Activity says: ${said}`).toBe(true);
    }
    console.log(`tv.episode.${kind}: finished ${outcome.finishedSec} s, handed off ${outcome.handedSec} s, imported ${outcome.importedSec} s after the search`);
  });
}

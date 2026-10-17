import { expect, test } from "@playwright/test";
import {
  FOLDERS, containerOf, docker, filesUnder, makeFilm, setUpStack, until,
  type FixtureFilm, type Stack
} from "./stack.ts";

/**
 * Group `films` (scripts/golden-path/suite/vm/scenarios/G02-Films.ps1): a film through qBittorrent, sent, downloaded, handed
 * to Weir, cleaned and imported; the film carries a small extra that is left out of the hand-off and stays in the client's
 * folder. Every check the VM scenarios make is made here, and what a client did is read from the client's own API.
 * (film.transmission, film.deluge, film.sabnzbd and film.nzbget wait for those clients to join the stack.)
 */
test.describe.configure({ mode: "serial" });

let stack: Stack;
let film: FixtureFilm;
let sentAt = 0;
let outcome: {
  finished: boolean; finishedSec: number; handedOff: boolean; handedSec: number;
  settled: boolean; imported: boolean; importedSec: number; handoff: any; file?: { path: string; size: number };
};

const seconds = () => Math.round((Date.now() - sentAt) / 100) / 10;

async function handoffOf(release: string) {
  const all = await stack.deluno.must<any[]>("GET", "/api/integrations/processors/handoffs?take=100");
  return all.find((h) => String(h.releaseName).includes(release));
}

test.beforeAll(async () => {
  test.setTimeout(10 * 60_000);
  stack = await setUpStack();
  film = await makeFilm(`E2eFilm${Date.now().toString(36)}`, true);
  const movie = await stack.deluno.must<any>("POST", "/api/movies", { title: film.title, releaseYear: film.year, imdbId: null, monitored: true });
  sentAt = Date.now();
  await stack.deluno.must("POST", `/api/movies/${movie.id}/grab`, {
    releaseName: film.release, indexerId: stack.indexerId, indexerName: "E2E feed", candidateQuality: "WEB 1080p",
    downloadUrl: film.url, sizeBytes: film.size, seeders: 50, force: true, overrideReason: "end-to-end suite", duplicateAction: null
  });

  // Wait for the whole journey, noting when each step is first seen (the VM's Wait-FilmImported).
  outcome = { finished: false, finishedSec: 0, handedOff: false, handedSec: 0, settled: false, imported: false, importedSec: 0, handoff: null };
  try {
    await until("the film to be imported", 5 * 60, async () => {
      if (!outcome.finished) {
        const t = await stack.qbittorrent.torrent(film.infohash);
        if (t && t.progress >= 1 && t.amount_left === 0) { outcome.finished = true; outcome.finishedSec = seconds(); }
      }
      const h = await handoffOf(film.release);
      if (h && !outcome.handedOff) { outcome.handedOff = true; outcome.handedSec = seconds(); }
      outcome.handoff = h;
      outcome.settled = !!h && h.status === "completed";
      const file = (await filesUnder("media", film.title))[0];
      if (file && !outcome.imported) { outcome.imported = true; outcome.importedSec = seconds(); outcome.file = file; }
      return outcome.imported && outcome.settled;
    }, 2000);
  } catch (error) {
    console.log(`The film did not finish: ${error}; hand-off ${JSON.stringify(outcome.handoff)?.slice(0, 800)}`);
  }
});

test("film.qbittorrent: a film through qBittorrent is grabbed, downloaded, handed to Weir and imported", async () => {
  expect(outcome.finished, "The client finished the download").toBe(true);
  expect(outcome.handedOff, "Deluno handed it to Weir").toBe(true);
  expect(outcome.settled, `Weir cleaned it and the hand-off settled (status ${outcome.handoff?.status}, processor said ${outcome.handoff?.processor?.state})`).toBe(true);
  expect(outcome.imported, "Deluno imported the cleaned file into the library").toBe(true);
  expect(outcome.file?.path ?? "").toContain(FOLDERS.moviesLibrary.replace("/media", ""));
  expect(outcome.file!.size).toBeGreaterThan(1_000_000);

  // As the VM scenario reads it: each activity item as its whole JSON, matched without regard to case.
  const activity = await stack.deluno.must<any>("GET", "/api/activity?pageSize=500");
  const lines = (activity.items as any[]).map((i) => JSON.stringify(i).toLowerCase()).filter((m) => m.includes(film.title.toLowerCase()));
  for (const said of ["Sent to the download client", "Sent to the processor", "Imported into the library"]) {
    expect(lines.some((m) => m.includes(said.toLowerCase())), `Activity says: ${said}`).toBe(true);
  }
  console.log(`film.qbittorrent: finished ${outcome.finishedSec} s, handed off ${outcome.handedSec} s, imported ${outcome.importedSec} s after the send`);
});

test("film.extra-left-out: a small extra in the release is left out of the hand-off and stays in the client's folder", async () => {
  expect(outcome.imported, "The film was imported").toBe(true);
  const inClient = await filesUnder("downloads", "featurette");
  expect(inClient.some((f) => f.path.includes(film.release)), "The extra (featurette.mkv) is still in the client's completed folder with the release").toBe(true);
  expect((await filesUnder("media", "featurette")).length, "The extra is not in the library").toBe(0);
  expect((await filesUnder("weir", "featurette")).length, "The extra is not in Weir's output").toBe(0);
  expect(outcome.handoff?.status, "The hand-off settled").toBe("completed");
  expect(String(outcome.handoff?.outputPath ?? ""), "Weir cleaned the film (the hand-off carries the output path Weir gave it, named for the film)").toContain(film.title);

  const log = docker("logs", containerOf("weir"));
  const combined = log;
  expect(combined, "Weir's log shows it reported a completed hand-off to Deluno").toMatch(/Hand-off callback: reported completed/);
  expect(combined, "Weir's log never names the extra").not.toMatch(/featurette/);
});

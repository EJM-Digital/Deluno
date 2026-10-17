import { expect, test } from "@playwright/test";
import { CLIENT_KINDS, isUsenet, type ClientKind } from "./clients.ts";
import {
  FOLDERS, clientFinished, containerOf, docker, filesUnder, makeFilm, routeLibraryTo, setUpStack, until,
  type FixtureFilm, type Stack
} from "./stack.ts";

/**
 * Group `films` (scripts/golden-path/suite/vm/scenarios/G02-Films.ps1): a film through each of the five clients, sent,
 * downloaded, handed to Weir, cleaned and imported; qBittorrent's film carries a small extra that is left out of the hand-off
 * and stays in the client's folder. All five films are sent first, so the clients and Weir work at once, as on the VM; each
 * client's test then reads its own film. Every check the VM scenarios make is made here, and what a client did is read from
 * the client's own API.
 *
 * How each client downloads with no internet: the torrent clients (qBittorrent, Transmission, Deluge) are given a torrent with
 * no tracker that names the fixture as its web seed (BEP 19), so every byte comes over HTTP from the fixture; SABnzbd and NZBGet
 * are given an .nzb of yEnc articles that the fixture's news server (NNTP) serves.
 */
test.describe.configure({ mode: "serial" });

interface Outcome {
  finished: boolean; finishedSec: number; handedOff: boolean; handedSec: number;
  settled: boolean; imported: boolean; importedSec: number; handoff: any; file?: { path: string; size: number };
}
interface Run { kind: ClientKind; film: FixtureFilm; sentAt: number; outcome: Outcome }

let stack: Stack;
const runs = new Map<ClientKind, Run>();

const secondsSince = (run: Run) => Math.round((Date.now() - run.sentAt) / 100) / 10;

async function handoffOf(release: string) {
  const all = await stack.deluno.must<any[]>("GET", "/api/integrations/processors/handoffs?take=100");
  return all.find((h) => String(h.releaseName).includes(release));
}

/** The VM's Wait-FilmImported for one film, noting when each step is first seen. */
async function watch(run: Run) {
  const { film, kind, outcome } = run;
  if (!outcome.finished && await clientFinished(stack, kind, film)) { outcome.finished = true; outcome.finishedSec = secondsSince(run); }
  const h = await handoffOf(film.release);
  if (h && !outcome.handedOff) { outcome.handedOff = true; outcome.handedSec = secondsSince(run); }
  outcome.handoff = h;
  outcome.settled = !!h && h.status === "completed";
  // A file Deluno is still copying is a .tmp beside the final name; only the finished file counts.
  const file = (await filesUnder("media", film.title)).find((f) => !/[.]tmp$/i.test(f.path));
  if (file && !outcome.imported) { outcome.imported = true; outcome.importedSec = secondsSince(run); outcome.file = file; }
  return outcome.imported && outcome.settled;
}

test.beforeAll(async () => {
  test.setTimeout(15 * 60_000);
  stack = await setUpStack();
  const stamp = Date.now().toString(36);

  // Send everything first: each film is routed to its own client, then sent as "Send anyway" on the title's page does.
  for (const kind of CLIENT_KINDS) {
    const title = `E2eFilm${stamp}${kind[0].toUpperCase()}${kind.slice(1)}`;
    const film = await makeFilm(title, kind === "qbittorrent", isUsenet(kind));
    const movie = await stack.deluno.must<any>("POST", "/api/movies", { title: film.title, releaseYear: film.year, imdbId: null, monitored: true });
    await routeLibraryTo(stack, kind);
    const sentAt = Date.now();
    await stack.deluno.must("POST", `/api/movies/${movie.id}/grab`, {
      releaseName: film.release, indexerId: isUsenet(kind) ? stack.usenetIndexerId : stack.indexerId,
      indexerName: isUsenet(kind) ? "E2E Usenet feed" : "E2E feed", candidateQuality: "WEB 1080p",
      downloadUrl: film.url, sizeBytes: film.size, seeders: isUsenet(kind) ? 0 : 50, force: true, overrideReason: "end-to-end suite", duplicateAction: null
    });
    runs.set(kind, {
      kind, film, sentAt,
      outcome: { finished: false, finishedSec: 0, handedOff: false, handedSec: 0, settled: false, imported: false, importedSec: 0, handoff: null }
    });
  }

  // Wait for the whole journey of every film.
  try {
    await until("every film to be imported", 8 * 60, async () => {
      let all = true;
      for (const run of runs.values()) {
        if (!await watch(run)) all = false;
      }
      return all;
    }, 2000);
  } catch (error) {
    console.log(`A film did not finish: ${error}`);
    for (const run of runs.values()) console.log(`  ${run.kind}: ${JSON.stringify({ ...run.outcome, handoff: undefined })}; hand-off ${JSON.stringify(run.outcome.handoff)?.slice(0, 600)}`);
  }
});

for (const kind of CLIENT_KINDS) {
  test(`film.${kind}: a film through ${kind} is grabbed, downloaded, handed to Weir and imported`, async () => {
    const { film, outcome } = runs.get(kind)!;
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
    console.log(`film.${kind}: finished ${outcome.finishedSec} s, handed off ${outcome.handedSec} s, imported ${outcome.importedSec} s after the send`);
  });
}

test("film.extra-left-out: a small extra in the release is left out of the hand-off and stays in the client's folder", async () => {
  const { film, outcome } = runs.get("qbittorrent")!;
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

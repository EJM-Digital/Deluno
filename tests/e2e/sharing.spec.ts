import { expect, test } from "@playwright/test";
import { type ClientKind } from "./clients.ts";
import { clientHolds, completedLeftovers, sendFilm, sleep, waitImported, type Sent } from "./flow.ts";
import { clientPaused } from "./queue.ts";
import { containerOf, docker, filesUnder, setUpStack, until, type FixtureFilm, type Stack } from "./stack.ts";

/**
 * Group `sharing` (#1293, the automation area's "when a download finishes" row): what Deluno does with the download client's own copy once a
 * film is safely in the library, each choice proven on the three real torrent clients (qBittorrent, Transmission, Deluge). For each choice
 * one film goes through every client to the library, and then the client is asked itself (never Deluno):
 *   - "Tidy up as soon as it is in the library", finishing by removing it: the client no longer holds the torrent and its files are gone from
 *     the client's finished folder;
 *   - "Keep sharing until the target is met", then stopping it (not removing it): the target is a share ratio, met by a second client of the stack
 *     that is pointed at the first and downloads the film from it. The client still holds the torrent, now stopped, and its files are still in the
 *     finished folder;
 *   - "Leave it alone": after several passes of the reclaim task the client still holds the torrent, still seeding, its files untouched.
 * In every case the library copy is still there, the same size. The settings are put back at the end through Deluno's own API.
 * (Usenet clients have no seeding to stop; their removal after an import is proven by the removal groups.)
 */
test.describe.configure({ mode: "serial" });

const TORRENTS: ClientKind[] = ["qbittorrent", "transmission", "deluge"];
let stack: Stack;
let original: any;
/** Torrents this group added to a second client so that the first one uploads: removed again at the end. */
const leeches: Array<{ client: "qbittorrent" | "deluge"; hash: string }> = [];

const ipOf = (kind: ClientKind) => docker("inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", containerOf(kind)).trim();

/**
 * Makes `kind` upload the film: another torrent client of the stack is given the torrent as a magnet link, with no web seed, and told the address
 * of `kind`'s listening port, so every byte it downloads comes from `kind` (Deluge reaches qBittorrent's and Transmission's films; qBittorrent
 * reaches Deluge's). The share ratio `kind` reports then rises by itself, which is what a sharing target waits for.
 */
async function pullFrom(kind: ClientKind, film: FixtureFilm) {
  const hash = film.infohash.toLowerCase();
  const magnet = `magnet:?xt=urn:btih:${hash}&dn=${encodeURIComponent(film.release)}`;
  const port = kind === "qbittorrent" ? (await stack.qbittorrent.get<any>("/api/v2/app/preferences")).listen_port
    : kind === "transmission" ? 51413 : await stack.deluge.call("core.get_listen_port");
  const address = `${ipOf(kind)}:${port}`;
  if (kind === "deluge") {
    await stack.qbittorrent.post("/api/v2/torrents/add", { urls: magnet, savepath: "/downloads/other", autoTMM: "false" });
    leeches.push({ client: "qbittorrent", hash });
    await until("qBittorrent to list the magnet", 30, async () => !!await stack.qbittorrent.torrent(hash), 1000);
    await stack.qbittorrent.post("/api/v2/torrents/addPeers", { hashes: hash, peers: address });
  } else {
    await stack.deluge.call("core.add_torrent_magnet", [magnet, { download_location: "/downloads/other" }]);
    leeches.push({ client: "deluge", hash });
    await until("Deluge to list the magnet", 30, async () => !!await stack.deluge.torrent(hash), 1000);
    await stack.deluge.call("core.connect_peer", [hash, ip(address), Number(address.split(":")[1])]);
  }
}
const ip = (address: string) => address.split(":")[0];

async function setSharing(patch: Record<string, unknown>) {
  await stack.deluno.must("PATCH", "/api/settings", patch);
}

/** One film through each torrent client to the library, under whatever the sharing settings now are. */
async function filmsThroughEveryTorrentClient(label: string) {
  const sends = new Map<ClientKind, Sent>();
  for (const kind of TORRENTS) sends.set(kind, await sendFilm(stack, kind, label, "file"));
  const libraryFiles = new Map<ClientKind, { path: string; size: number }>();
  for (const [kind, sent] of sends) libraryFiles.set(kind, (await waitImported(stack, sent)).file);
  return { sends, libraryFiles };
}

async function libraryCopyIsUntouched(sent: Sent, before: { path: string; size: number }) {
  const now = (await filesUnder("media", sent.film.title)).find((f) => f.path === before.path);
  expect(now, `The library copy of ${sent.film.title} is still at ${before.path}`).toBeTruthy();
  expect(now!.size, "The library copy is the same size").toBe(before.size);
}

test.beforeAll(async () => {
  test.setTimeout(15 * 60_000);
  stack = await setUpStack();
  original = await stack.deluno.must("GET", "/api/settings");
});

test.afterAll(async () => {
  for (const leech of leeches) {
    try {
      if (leech.client === "deluge") await stack.deluge.call("core.remove_torrent", [leech.hash, true]);
      else await stack.qbittorrent.post("/api/v2/torrents/delete", { hashes: leech.hash, deleteFiles: "true" });
    } catch { /* already gone */ }
  }
  if (!original) return;
  await setSharing({
    sharingMode: original.sharingMode, sharingForHours: original.sharingForHours, sharingUntilRatio: original.sharingUntilRatio ?? 0,
    sharingCompletionAction: original.sharingCompletionAction, sharingStuckAfterDays: original.sharingStuckAfterDays
  });
});

test("sharing.reclaim: tidying up removes the torrent and its files from each client and leaves the library copy", async () => {
  test.setTimeout(20 * 60_000);
  await setSharing({ sharingMode: "tidy-now", sharingCompletionAction: "reclaim" });
  const { sends, libraryFiles } = await filmsThroughEveryTorrentClient("ShareReclaim");
  for (const [kind, sent] of sends) {
    const gone = await until(`${kind} to let go of ${sent.film.release}`, 180, async () => !(await clientHolds(stack, kind, sent.film)), 2000).catch(() => undefined);
    expect(gone, `${kind} itself no longer holds the torrent`).toBeTruthy();
    const leftovers = await until(`${kind}'s finished folder to be emptied of ${sent.film.title}`, 60, async () => (await completedLeftovers(sent.film.title)).length === 0, 2000).catch(() => undefined);
    expect(leftovers, `${kind}'s files are gone from the client's finished folder`).toBeTruthy();
    await libraryCopyIsUntouched(sent, libraryFiles.get(kind)!);
  }
});

test("sharing.stop: stopping keeps the torrent and its files in each client, stopped, and leaves the library copy", async () => {
  test.setTimeout(20 * 60_000);
  // Keep sharing until a share ratio of 0.5 (no time target), then stop it.
  await setSharing({ sharingMode: "share-then-tidy", sharingForHours: 0, sharingUntilRatio: 0.5, sharingCompletionAction: "stop" });
  const { sends, libraryFiles } = await filmsThroughEveryTorrentClient("ShareStop");
  // While nothing has downloaded from them, the target is not met: every client keeps its torrent running.
  await sleep(70_000);
  for (const [kind, sent] of sends) expect(await clientPaused(stack, kind, sent), `${kind} keeps sharing while the target is not met`).toBe(false);
  for (const [kind, sent] of sends) await pullFrom(kind, sent.film);
  for (const [kind, sent] of sends) {
    const stopped = await until(`${kind} to stop ${sent.film.release}`, 180, async () => (await clientPaused(stack, kind, sent)) === true, 2000).catch(() => undefined);
    expect(stopped, `${kind} itself reports the torrent stopped`).toBeTruthy();
    expect(await clientHolds(stack, kind, sent.film), `${kind} still holds the torrent`).toBe(true);
    expect((await completedLeftovers(sent.film.title)).length, `${kind}'s files are still in the finished folder`).toBeGreaterThan(0);
    await libraryCopyIsUntouched(sent, libraryFiles.get(kind)!);
  }
});

test("sharing.leave-alone: leaving it alone never touches the client's copy, however many passes run", async () => {
  test.setTimeout(20 * 60_000);
  await setSharing({ sharingMode: "leave-alone" });
  const { sends, libraryFiles } = await filmsThroughEveryTorrentClient("ShareLeave");
  // The reclaim task runs every 30 seconds: wait through several passes after the import.
  await sleep(100_000);
  for (const [kind, sent] of sends) {
    expect(await clientHolds(stack, kind, sent.film), `${kind} still holds the torrent`).toBe(true);
    expect(await clientPaused(stack, kind, sent), `${kind} has not stopped it`).toBe(false);
    expect((await completedLeftovers(sent.film.title)).length, `${kind}'s files are untouched`).toBeGreaterThan(0);
    await libraryCopyIsUntouched(sent, libraryFiles.get(kind)!);
  }
});

import { expect, test } from "@playwright/test";
import { CLIENT_KINDS, type ClientKind } from "./clients.ts";
import { clientHolds, completedLeftovers, handoffOf, importedFile, sendFilm, waitImported, activityLines, sleep, type Sent } from "./flow.ts";
import { setUpStack, until, type Stack } from "./stack.ts";

/**
 * Group `removal` (scripts/golden-path/suite/vm/scenarios/G03-Removal.ps1): "Remove this copy" on a finished download, as a
 * folder on every client and as a single file on the three torrent clients; removing twice reads as a failure and writes no
 * second Activity row. Each film is its own (the VM sends one per scenario); all eight are sent first so the clients and Weir
 * work at once. What the VM reads from the folders is read here from the shared folders, and the client's own API is asked
 * too whether it still holds the download.
 */
test.describe.configure({ mode: "serial" });

const TORRENT_KINDS: ClientKind[] = ["qbittorrent", "transmission", "deluge"];
interface Case { id: string; what: string; kind: ClientKind; layout: "folder" | "file" }
const cases: Case[] = [
  ...CLIENT_KINDS.map((kind): Case => ({ id: `remove-copy.${kind}.folder`, what: `${kind} folder`, kind, layout: "folder" })),
  ...TORRENT_KINDS.map((kind): Case => ({ id: `remove-copy.${kind}.file`, what: `${kind} single file`, kind, layout: "file" }))
];

let stack: Stack;
const sent = new Map<string, Sent>();
const imported = new Map<string, { file: { path: string; size: number } | undefined; waited: boolean }>();

test.beforeAll(async () => {
  test.setTimeout(15 * 60_000);
  stack = await setUpStack();
  for (const c of cases) sent.set(c.id, await sendFilm(stack, c.kind, "Removal", c.layout));
  for (const c of cases) {
    try {
      const done = await waitImported(stack, sent.get(c.id)!);
      imported.set(c.id, { file: done.file, waited: true });
    } catch (error) {
      console.log(`${c.id}: the film was not imported first: ${error}`);
      imported.set(c.id, { file: undefined, waited: false });
    }
  }
});

for (const c of cases) {
  test(`${c.id}: Remove this copy on a finished ${c.what} download, and removing twice`, async () => {
    const { film } = sent.get(c.id)!;
    expect(imported.get(c.id)!.waited, "The film was imported first").toBe(true);

    const before = await completedLeftovers(film.title);
    expect(before.length, `${c.what}: the download is on disk before it is removed (${before.length} items)`).toBeGreaterThan(0);
    expect(await clientHolds(stack, c.kind, film), `${c.what}: the client still holds the finished download before it is removed`).toBe(true);
    const library = await importedFile(film.title);
    const libraryBytes = library?.size ?? -1;
    expect(libraryBytes, "the film is in the library before the removal").toBeGreaterThan(0);
    const removedRows = async () => (await activityLines(stack, film.title)).filter((l) => l.includes("removed from the download queue")).length;
    const rowsBefore = await removedRows();

    const handoff = await handoffOf(stack, film.release);
    expect(handoff?.queueItemId, "Deluno knows the download's id in the client").toBeTruthy();
    const remove = () => stack.deluno.call<any>("POST", `/api/download-clients/${handoff.clientId}/queue/${encodeURIComponent(handoff.queueItemId)}/remove`, { reason: "reclaim" });

    const first = await remove();
    expect(first.status === 200 && first.body?.succeeded, `${c.what}: the first removal succeeds (HTTP ${first.status}: ${first.body?.message})`).toBeTruthy();
    const gone = await until(`nothing of ${film.title} to be left under the completed folders`, 20, async () => (await completedLeftovers(film.title)).length === 0, 500).catch(() => undefined);
    const left = await completedLeftovers(film.title);
    expect(gone, `${c.what}: no folder, file or extra of it is left (left: ${left.slice(0, 5).map((f) => f.path).join("; ")})`).toBeTruthy();
    expect(await clientHolds(stack, c.kind, film), `${c.what}: the client itself no longer holds the download`).toBe(false);
    expect((await importedFile(film.title))?.size, `${c.what}: the library file was not touched`).toBe(libraryBytes);

    const second = await remove();
    const reads = second.status >= 400 || second.body?.succeeded === false;
    expect(reads, `${c.what}: the second removal reads as a failure (HTTP ${second.status}: ${second.body?.message ?? second.text})`).toBe(true);
    await sleep(2000);
    expect(await removedRows(), `${c.what}: the second removal writes no second Activity row (rows before the first removal ${rowsBefore})`).toBe(rowsBefore + 1);
  });
}

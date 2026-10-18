import { expect, test } from "@playwright/test";
import { CLIENT_KINDS, type ClientKind } from "./clients.ts";
import { activityLines, clientHolds, openDeluno, telemetryRows, type Sent } from "./flow.ts";
import { blockedFor, filesOnDisk, openDownload, removeWith, sendStalled, waitInTransfers } from "./queue.ts";
import { setUpStack, until, type Stack } from "./stack.ts";

/**
 * Group `queueremove` (Transfers, "Remove from client"): on a real download in each of the five clients, pressed on the real
 * screen in a real browser, "It's bad" and "I don't want it" each do exactly what their words say, and each is confirmed from the
 * client's own API and the shared download folders:
 *  - It's bad: the client no longer holds it, its files are gone, the release is on the blocklist and a new search is started.
 *  - I don't want it: the client no longer holds it, its files are gone, nothing is blocklisted, no search is started and the film
 *    is no longer wanted.
 * The downloads are stalled ones (a torrent stopped just short of the end, a Usenet job whose last article never comes), so no
 * hand-off is involved and the row is in motion when the button is pressed.
 */
test.describe.configure({ mode: "serial" });

type Choice = "bad" | "unwanted";
const WORDS: Record<Choice, "It's bad" | "I don't want it"> = { bad: "It's bad", unwanted: "I don't want it" };

let stack: Stack;
const sends = new Map<string, Sent>();
const key = (kind: ClientKind, choice: Choice) => `${choice}.${kind}`;

test.beforeAll(async () => {
  test.setTimeout(15 * 60_000);
  stack = await setUpStack();
  for (const kind of CLIENT_KINDS) for (const choice of ["bad", "unwanted"] as Choice[]) sends.set(key(kind, choice), await sendStalled(stack, kind, choice === "bad" ? "Bad" : "Unwanted"));
  for (const sent of sends.values()) await waitInTransfers(stack, sent);
});

for (const choice of ["bad", "unwanted"] as Choice[]) {
  for (const kind of CLIENT_KINDS) {
    test(`queueremove.${choice}.${kind}: "${WORDS[choice]}" on a ${kind} download does what it says, as the client itself shows`, async ({ browser }) => {
      test.setTimeout(5 * 60_000);
      const sent = sends.get(key(kind, choice))!;
      const { film } = sent;
      expect(await clientHolds(stack, kind, film), `${kind} holds the download before the button is pressed`).toBe(true);
      expect(await filesOnDisk(film.title), `${kind} has files of the download on disk before the button is pressed`).toBeGreaterThan(0);

      const page = await openDeluno(browser, stack, "/queue");
      try {
        await openDownload(page, sent);
        const answer = await removeWith(page, WORDS[choice]);
        expect(answer.sent, `The screen asked for "${choice === "bad" ? "failed" : "unwanted"}" for this download`).toMatchObject({ reason: choice === "bad" ? "failed" : "unwanted" });
        expect(answer.status === 200 && answer.body?.succeeded, `Deluno accepted the removal (HTTP ${answer.status}: ${answer.body?.message})`).toBeTruthy();
        expect(answer.toast, "The toast carries the server's sentence").toBe(true);
        // The dialog and the drawer close once the server has done what was asked.
        await expect(page.getByRole("button", { name: "Remove from client" }), "The drawer closes after the removal").toBeHidden({ timeout: 60_000 });
      } finally { await page.context().close(); }

      const gone = await until(`${kind} to stop holding ${film.release}`, 60, async () => !(await clientHolds(stack, kind, film)), 1000).catch(() => undefined);
      expect(gone, `${kind} itself no longer holds the download`).toBeTruthy();
      const left = await until(`the files of ${film.title} to be gone`, 60, async () => (await filesOnDisk(film.title)) === 0, 1000).catch(() => undefined);
      expect(left, `${kind}'s files are deleted (${await filesOnDisk(film.title)} left)`).toBeTruthy();
      const unlisted = await until(`Transfers to stop listing ${film.release}`, 60, async () => !(await telemetryRows(stack)).some((r) => String(r.releaseName ?? "").includes(film.release)), 1000).catch(() => undefined);
      expect(unlisted, "Transfers no longer lists it").toBeTruthy();

      const blocked = await blockedFor(stack, film.release);
      const lines = await activityLines(stack, film.title);
      if (choice === "bad") {
        expect(blocked.length, "The release is on the blocklist").toBeGreaterThan(0);
        expect(lines.some((l) => l.includes("marked") && l.includes("as bad") && l.includes("started a new search")), "Activity says it was marked bad and a new search started").toBe(true);
      } else {
        expect(blocked.length, "Nothing is blocklisted").toBe(0);
        expect(lines.some((l) => l.includes("don't want") && !l.includes("new search")), "Activity says the person does not want it, with no search").toBe(true);
        const movie = await stack.deluno.must<any>("GET", `/api/movies/${sent.movieId}`);
        expect(movie.monitored, "The film is no longer wanted").toBe(false);
      }
    });
  }
}

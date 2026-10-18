import { expect, test } from "@playwright/test";
import { CLIENT_KINDS } from "./clients.ts";
import { activityLines, clientHolds, openDeluno, telemetryRows, type Sent } from "./flow.ts";
import { blockedFor, clientPaused, filesOnDisk, openDownload, pressAction, removeWith, sendStalled, waitInTransfers } from "./queue.ts";
import { setUpStack, until, type Stack } from "./stack.ts";

/**
 * Group `queuehold` (Transfers, "In the client" and "Remove from client"): on a real download in each of the five clients, on the
 * real screen in a real browser, Pause pauses it and Resume starts it again, each as the client's own API shows; then "Just stop
 * tracking it" makes Deluno forget the download while the client keeps it, running as it was, and its files untouched.
 */
test.describe.configure({ mode: "serial" });

let stack: Stack;
const sends = new Map<string, Sent>();

test.beforeAll(async () => {
  test.setTimeout(15 * 60_000);
  stack = await setUpStack();
  for (const kind of CLIENT_KINDS) sends.set(kind, await sendStalled(stack, kind, "Hold", "all"));
  for (const sent of sends.values()) await waitInTransfers(stack, sent);
});

for (const kind of CLIENT_KINDS) {
  test(`queuehold.${kind}: Pause and Resume on a ${kind} download, then Just stop tracking it, each as the client itself shows`, async ({ browser }) => {
    test.setTimeout(6 * 60_000);
    const sent = sends.get(kind)!;
    const { film } = sent;
    expect(await clientPaused(stack, kind, sent), `${kind} is running the download before anything is pressed`).toBe(false);

    const page = await openDeluno(browser, stack, "/queue");
    try {
      await openDownload(page, sent);

      const pause = await pressAction(page, "Pause");
      expect(pause.sent, "Pause asks the client to pause this download").toMatchObject({ action: "pause" });
      expect(pause.status, "Deluno accepts the pause").toBe(200);
      expect(pause.toast, "The toast says Pause was sent").toBe(true);
      const paused = await until(`${kind} to pause ${film.release}`, 60, async () => (await clientPaused(stack, kind, sent)) === true, 1000).catch(() => undefined);
      expect(paused, `Pause: ${kind} itself reports the download paused`).toBeTruthy();

      const resume = await pressAction(page, "Resume");
      expect(resume.sent, "Resume asks the client to resume this download").toMatchObject({ action: "resume" });
      expect(resume.status, "Deluno accepts the resume").toBe(200);
      expect(resume.toast, "The toast says Resume was sent").toBe(true);
      const resumed = await until(`${kind} to resume ${film.release}`, 60, async () => (await clientPaused(stack, kind, sent)) === false, 1000).catch(() => undefined);
      expect(resumed, `Resume: ${kind} itself reports the download running again`).toBeTruthy();

      const filesBefore = await filesOnDisk(film.title);
      const answer = await removeWith(page, "Just stop tracking it");
      expect(answer.sent, "The screen asked for forget").toMatchObject({ reason: "forget" });
      expect(answer.status === 200 && answer.body?.succeeded, `Deluno accepted it (HTTP ${answer.status}: ${answer.body?.message})`).toBeTruthy();
      expect(answer.toast, "The toast carries the server's sentence").toBe(true);
      await expect(page.getByRole("button", { name: "Remove from client" }), "The drawer closes after the choice").toBeHidden({ timeout: 60_000 });

      const closed = await until(`Deluno to close its record of ${film.release}`, 60, async () => {
        const open = await stack.deluno.must<any>("GET", `/api/download-dispatches?entityId=${sent.movieId}&pageSize=50`);
        return ((open.items ?? open) as any[]).length === 0;
      }, 1000).catch(() => undefined);
      expect(closed, "Deluno closed its record of the send (the film is free for a new one)").toBeTruthy();
      expect(await clientHolds(stack, kind, film), `${kind} still holds the download`).toBe(true);
      // #1356: it is outside Deluno's view now, so Transfers no longer lists it, though the client still holds it.
      const gone = await until(`Transfers to stop listing ${film.release}`, 60, async () =>
        !(await telemetryRows(stack)).some((r) => String(r.releaseName ?? "").includes(film.release) || String(r.title ?? "").includes(film.title)), 1000).catch(() => undefined);
      expect(gone, "Transfers no longer lists the download Deluno stopped tracking").toBeTruthy();
      expect(await filesOnDisk(film.title), `${kind}'s files are untouched (${filesBefore} before)`).toBeGreaterThanOrEqual(filesBefore);
      expect((await blockedFor(stack, film.release)).length, "Nothing is blocklisted").toBe(0);
      const lines = await activityLines(stack, film.title);
      expect(lines.some((l) => l.includes("stop tracking") && l.includes("untouched")), "Activity says Deluno stopped tracking it and the client still has it").toBe(true);
    } finally { await page.context().close(); }
  });
}

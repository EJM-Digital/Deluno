import { expect, test, type Page } from "@playwright/test";
import { CLIENT_KINDS, isUsenet, type ClientKind } from "./clients.ts";
import { handoffOf, openDeluno, pageText, restoreWeir, sendFilm, setAutomation, sleep, telemetryRows, waitImported, type Sent } from "./flow.ts";
import { setWeirPause } from "./resend.ts";
import { clientFinished, setUpStack, until, type Stack } from "./stack.ts";

/**
 * Group `queuehandover` (Transfers, the "Handing over" state): a download the client has finished, with Deluno's automation held
 * so no hand-off exists yet, reads "Handing over" on its row, never "Waiting for processor". Once automation runs and Weir is held,
 * the hand-off exists and the row no longer says "Handing over". Weir is then let go and the film is imported. Every client's film
 * goes through it. The client's own API says when the download finished.
 */
test.describe.configure({ mode: "serial" });

let stack: Stack;
const films = new Map<ClientKind, Sent>();
let automationOn = false;

test.beforeAll(async () => {
  test.setTimeout(15 * 60_000);
  stack = await setUpStack();
  await setAutomation(stack, false);
  for (const kind of CLIENT_KINDS) films.set(kind, await sendFilm(stack, kind, "Handover", "file"));
  for (const kind of CLIENT_KINDS) await until(`${kind} to finish ${films.get(kind)!.film.release}`, 240, () => clientFinished(stack, kind, films.get(kind)!.film), 1000);
  // A person would not look the instant the client finished: Deluno's own check interval is the behaviour being allowed for.
  await sleep(15_000);
});

test.afterAll(async () => {
  if (!automationOn) await setAutomation(stack, true).catch(() => undefined);
  await setWeirPause(false).catch(() => undefined);
  await restoreWeir().catch(() => undefined);
});

const rowOf = (page: Page, sent: Sent) => page.getByRole("row").filter({ hasText: sent.film.title }).first();

for (const kind of CLIENT_KINDS) {
  test(`queuehandover.before.${kind}: A finished ${kind} download with no hand-off yet is never read as Waiting for processor`, async ({ browser }) => {
    test.setTimeout(3 * 60_000);
    const sent = films.get(kind)!;
    expect(await handoffOf(stack, sent.film.release), "No hand-off exists yet (automation is held)").toBeUndefined();
    const page = await openDeluno(browser, stack, "/queue");
    try {
      if (!isUsenet(kind)) {
        // A torrent stays in the client (it seeds), so its finished download keeps its row in Transfers until it is handed over.
        const row = rowOf(page, sent);
        await expect(row, `The ${kind} row says Handing over`).toContainText("Handing over", { timeout: 60_000 });
        expect(await row.innerText(), "The row never says Waiting for processor").not.toMatch(/Waiting for processor/i);
      } else {
        // A finished Usenet job leaves the client's queue for its history, so it has no row until its hand-off exists.
        await expect.poll(() => pageText(page), { timeout: 60_000, message: "Transfers has loaded" }).toMatch(/in flight|Nothing in motion/i);
        await sleep(3000);
      }
      expect(await pageText(page), "The page never says Waiting for processor").not.toMatch(/Waiting for processor/i);
    } finally { await page.context().close(); }
  });
}

test("queuehandover.start: Automation runs and Weir is held, so every finished download has a hand-off in progress", async () => {
  test.setTimeout(5 * 60_000);
  await setWeirPause(true);
  await setAutomation(stack, true);
  automationOn = true;
  for (const kind of CLIENT_KINDS) {
    const { film } = films.get(kind)!;
    const h = await until(`the hand-off of ${film.release}`, 150, () => handoffOf(stack, film.release), 1000).catch(() => undefined);
    expect(h?.value, `${kind}: Deluno made the hand-off`).toBeTruthy();
  }
});

for (const kind of CLIENT_KINDS) {
  test(`queuehandover.during.${kind}: A ${kind} download whose hand-off is in progress no longer reads Handing over`, async ({ browser }) => {
    test.setTimeout(3 * 60_000);
    const sent = films.get(kind)!;
    expect(await handoffOf(stack, sent.film.release), "The hand-off exists").toBeTruthy();
    const status = (await telemetryRows(stack)).find((r) => String(r.releaseName ?? "").includes(sent.film.release))?.status;
    const page = await openDeluno(browser, stack, "/queue");
    try {
      // The download (a torrent's row) or its hand-off (a Usenet job's, which has left the client) is on the screen by its title.
      await expect(page.getByText(sent.film.title).first(), `The ${kind} download or its hand-off is on the screen`).toBeVisible({ timeout: 60_000 });
      await expect.poll(() => pageText(page), { timeout: 60_000, message: `Nothing on the page says Handing over once the hand-off exists (Deluno's own status ${status})` }).not.toMatch(/Handing over/);
    } finally { await page.context().close(); }
  });
}

test("queuehandover.finish: Weir is let go and every film is imported", async () => {
  test.setTimeout(10 * 60_000);
  await setWeirPause(false);
  for (const kind of CLIENT_KINDS) {
    const done = await waitImported(stack, films.get(kind)!);
    expect(done.file, `${kind}: the film is in the library`).toBeTruthy();
  }
});

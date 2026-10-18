import type { Page } from "@playwright/test";
import { isUsenet, type ClientKind } from "./clients.ts";
import { clientHolds, sendFilm, telemetryRows, type Sent } from "./flow.ts";
import { filesUnder, until, type Stack } from "./stack.ts";

/**
 * What the Transfers (queue) groups share: a download that stays in its client (a torrent stopped just short of the end, a Usenet
 * job whose last article is never answered), the real Transfers screen driven the way a person does it, and each client's OWN
 * word on what a button did (paused, held, gone). Nothing here asks Deluno what another application did.
 */

/** Sends a film that the client keeps downloading but never finishes, so the Transfers row stays in motion (a Usenet job that never gets its last article, or none at all). */
export function sendStalled(stack: Stack, kind: ClientKind, label: string, usenetHang: "all" | "last" = "last"): Promise<Sent> {
  return isUsenet(kind)
    ? sendFilm(stack, kind, label, "folder", { hang: usenetHang })
    : sendFilm(stack, kind, label, "file", { variant: "short" });
}

/** Waits until the client holds the download and Deluno's Transfers lists it (so the row is on the screen). */
export async function waitInTransfers(stack: Stack, sent: Sent, seconds = 180) {
  const { film, kind } = sent;
  await until(`${kind} to hold ${film.release}`, seconds, () => clientHolds(stack, kind, film), 1000);
  return (await until(`Transfers to list ${film.release}`, seconds, async () =>
    (await telemetryRows(stack)).find((r) => String(r.releaseName ?? "").includes(film.release) || String(r.title ?? "").includes(film.title)), 1000)).value;
}

/** The client's OWN word: true paused, false not paused, undefined when it does not hold the download. */
export async function clientPaused(stack: Stack, kind: ClientKind, sent: Sent): Promise<boolean | undefined> {
  const { film } = sent;
  switch (kind) {
    case "qbittorrent": {
      const t = await stack.qbittorrent.torrent(film.infohash);
      return t ? /^(paused|stopped)/i.test(t.state) : undefined;
    }
    case "transmission": {
      const t = await stack.transmission.torrent(film.infohash);
      return t ? t.status === 0 : undefined;
    }
    case "deluge": {
      const t = await stack.deluge.torrent(film.infohash);
      return t ? t.state === "Paused" : undefined;
    }
    case "sabnzbd": {
      const queue = await stack.sabnzbd.call("queue");
      const slot = (queue.queue?.slots as any[] | undefined)?.find((s) => s.filename === film.release);
      return slot ? String(slot.status).toLowerCase() === "paused" : undefined;
    }
    case "nzbget": {
      const groups = await stack.nzbget.call("listgroups") as Array<{ NZBName: string; Status: string; RemainingSizeMB: number; PausedSizeMB: number }>;
      const g = groups.find((x) => x.NZBName === film.release);
      // NZBGet says DOWNLOADING for a moment after a pause while a connection finishes its article: paused is the status, or all that is left paused.
      return g ? /^PAUSED/i.test(g.Status) || (g.RemainingSizeMB > 0 && g.PausedSizeMB >= g.RemainingSizeMB) : undefined;
    }
  }
}

/** Opens the film's download in the Transfers drawer, as a person clicks its row. */
export async function openDownload(page: Page, sent: Sent) {
  await page.getByRole("row").filter({ hasText: sent.film.title }).first().click({ timeout: 60_000 });
  await page.getByRole("button", { name: "Remove from client" }).waitFor({ timeout: 30_000 });
}

const CHOICE_PATTERNS = { "It's bad": /: It.s bad$/, "I don't want it": /: I don.t want it$/, "Just stop tracking it": /: Just stop tracking it$/ };

export interface Pressed { status: number; body: any; sent: any; toast: boolean }

/** Waits for the toast that carries the sentence (a few seconds at most), true when it was on the screen. */
async function toastSaid(page: Page, text: string): Promise<boolean> {
  if (!text) return false;
  return await page.getByText(text, { exact: false }).first().waitFor({ state: "visible", timeout: 10_000 }).then(() => true, () => false);
}

/** Presses Remove from client and then one of the dialog's three choices, by the words on it. Returns what the screen sent, what Deluno answered and whether the toast said it. */
export async function removeWith(page: Page, choice: keyof typeof CHOICE_PATTERNS): Promise<Pressed> {
  await page.getByRole("button", { name: "Remove from client" }).click();
  const answered = page.waitForResponse((r) => /\/queue\/[^/]+\/remove$/.test(r.url()) && r.request().method() === "POST", { timeout: 60_000 });
  await page.getByRole("button", { name: CHOICE_PATTERNS[choice] }).click();
  const response = await answered;
  const body = await response.text().then((text) => { try { return JSON.parse(text); } catch { return undefined; } }, () => undefined);
  return { status: response.status(), body, sent: JSON.parse(response.request().postData() ?? "{}"), toast: await toastSaid(page, String(body?.message ?? "")) };
}

/** Presses one of the drawer's Pause or Resume buttons. Returns what the screen sent, what Deluno answered and whether the toast said "<Button> sent to <client>". */
export async function pressAction(page: Page, button: "Pause" | "Resume"): Promise<Pressed> {
  const answered = page.waitForResponse((r) => /\/queue\/actions$/.test(r.url()) && r.request().method() === "POST", { timeout: 60_000 });
  await page.getByRole("button", { name: button, exact: true }).click();
  const response = await answered;
  const sent = JSON.parse(response.request().postData() ?? "{}");
  const toast = await page.getByText(`${button} sent to`).first().waitFor({ state: "visible", timeout: 10_000 }).then(() => true, () => false);
  return { status: response.status(), body: undefined, sent, toast };
}

/** What the files of the film under the clients' folders (completed and incomplete) come to. */
export async function filesOnDisk(title: string) {
  return (await filesUnder("downloads", title)).length;
}

/** The blocklist entries for a release, read from the blocklist's own route. */
export async function blockedFor(stack: Stack, release: string) {
  const all = await stack.deluno.must<any[]>("GET", "/api/blocked-releases");
  return all.filter((b) => String(b.releaseName ?? "").includes(release));
}

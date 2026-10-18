import { spawnSync } from "node:child_process";
import { expect } from "@playwright/test";
import type { ClientKind } from "./clients.ts";
import { activityLines, clientHolds, handoffOf, importedFile, sendFilm, sleep, weirSignIn, type Sent } from "./flow.ts";
import { URLS, clientFinished, containerOf, filesUnder, routeLibraryTo, until, type Stack } from "./stack.ts";

/**
 * What the `resend` groups share (scripts/golden-path/suite/vm/scenarios/G04-Resend.ps1, #1208, #1226, #1242, #1262): "I don't
 * want it", then a resend of the same release to the same client, in each timing. The new hand-off must settle on its own with
 * one import, the first send's late report must be quiet, and nothing may be left unmatched in Weir's output.
 *
 * Weir's pause (its own API) holds a first send so a moment is made, not waited for: a film is cleaned in about a second.
 */

export type Timing = "during" | "after";

/** Holds Weir's work (or lets it go) through Weir's own pause call, as its screen does. */
export async function setWeirPause(paused: boolean) {
  const weir = await weirSignIn();
  try {
    const reply = await weir.put("/api/v1/pause", { paused, scan_while_paused: true });
    if (reply.status >= 300) throw new Error(`Weir would not ${paused ? "pause" : "resume"}: ${reply.status} ${reply.text}`);
  } finally { await weir.dispose(); }
}

/** One container's log (stdout and stderr together) since a moment. */
export function containerLog(service: string, sinceMs?: number): string {
  const parts = (process.env.E2E_DOCKER ?? "docker").split(" ").filter(Boolean);
  const args = [...parts.slice(1), "logs", ...(sinceMs ? ["--since", new Date(sinceMs).toISOString()] : []), containerOf(service)];
  const run = spawnSync(parts[0], args, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024, timeout: 120_000 });
  return `${run.stdout ?? ""}\n${run.stderr ?? ""}`;
}

const GRAB_NOTE = "end-to-end suite: sending the same release again";

/** Sends the film's release again to the same client, as "Search now" on the title does after "I don't want it". */
export async function sendAgain(stack: Stack, sent: Sent) {
  const usenet = sent.film.protocol === "usenet";
  // The library is routed to one client at a time and other films were sent since, so route it back first.
  await routeLibraryTo(stack, sent.kind);
  return await stack.deluno.call("POST", sent.seriesId ? `/api/series/${sent.seriesId}/grab` : `/api/movies/${sent.movieId}/grab`, {
    releaseName: sent.film.release, indexerId: usenet ? stack.usenetIndexerId : stack.indexerId,
    indexerName: usenet ? "E2E Usenet feed" : "E2E feed", candidateQuality: "WEB 1080p",
    downloadUrl: sent.film.url, sizeBytes: sent.film.size, seeders: usenet ? 0 : 50, force: true, overrideReason: GRAB_NOTE, duplicateAction: null
  });
}

/** "I don't want it": Deluno removes the download from the client and deletes its data. */
export async function removeDownload(stack: Stack, sent: Sent, reason = "unwanted") {
  const h = await handoffOf(stack, sent.film.release);
  if (!h?.queueItemId) throw new Error(`Deluno has no download id in the client for ${sent.film.release}`);
  return await stack.deluno.call("POST", `/api/download-clients/${h.clientId}/queue/${encodeURIComponent(h.queueItemId)}/remove`, { reason });
}

/**
 * Sends the release again once the removed torrent is gone from its client, and waits for the client to finish it. The removal is
 * waited for first: a client that kept the torrent (Deluge has the same id for the same torrent) would read "finished" at once and
 * the second send would never be watched.
 */
export async function startAgainDownload(stack: Stack, sent: Sent) {
  if (sent.kind === "qbittorrent" || sent.kind === "transmission" || sent.kind === "deluge") {
    await until(`${sent.kind} to let go of ${sent.film.release}`, 30, async () => !(await clientHolds(stack, sent.kind, sent.film)), 500).catch(() => undefined);
  }
  const reply = await sendAgain(stack, sent);
  if (reply.status < 200 || reply.status >= 300) throw new Error(`Sending ${sent.film.release} again answered ${reply.status}. ${reply.text.slice(0, 400)}`);
  // The torrent is downloaded again from the web seed; the client's own word says when it has it.
  await until(`${sent.kind} to finish ${sent.film.release} after it was sent again`, 180, () => clientFinished(stack, sent.kind, sent.film), 1000);
}

export interface OpenSend { reached: boolean; sent: Sent; first?: any; what: string }

/**
 * Sends a single-file film and holds its first send at one moment: "during" (Weir is working on it) or "after" (Weir has put the
 * cleaned file in its output and Deluno has not queued the import). The first send is held at Weir (paused) until Weir is let go.
 * Up to three films are tried; the moment is missed when Weir is quicker than the check.
 */
export async function openFirstSend(stack: Stack, kind: ClientKind, timing: Timing, label = `re${timing}`, notes: string[] = []): Promise<OpenSend> {
  let last: OpenSend | undefined;
  for (let attempt = 1; attempt <= 3; attempt++) {
    let sent: Sent | undefined;
    let what = "";
    try {
      await setWeirPause(true);
      sent = await sendFilm(stack, kind, label, "file");
      await until(`${kind} to finish ${sent.film.release}`, 240, () => clientFinished(stack, kind, sent!.film), 1000);
      const queued = await until(`the first hand-off of ${sent.film.release}`, 120, () => handoffOf(stack, sent!.film.release), 500).catch(() => undefined);
      if (!queued) { what = "the first hand-off never appeared"; notes.push(`Attempt ${attempt}: ${what}.`); last = { reached: false, sent, what }; continue; }
      // Weir takes the first send now. During: removed and sent again straight away, while it works. After: once its cleaned file is
      // in its output, before Deluno has queued the import.
      await setWeirPause(false);
      if (timing === "after") {
        await until(`Weir to put the cleaned ${sent.film.release} in its output`, 60, async () => (await filesUnder("weir", sent!.film.title)).length > 0, 50).catch(() => undefined);
      }
      const h = await handoffOf(stack, sent.film.release);
      const open = !!h && !h.importJobId && (timing === "after" || h.status !== "completed");
      if (open) return { reached: true, sent, first: h, what: "" };
      what = `status '${h?.status}', import ${h?.importJobId ? "queued" : "not queued"}`;
      notes.push(`Attempt ${attempt}: the ${timing} moment was missed (${what}); trying a new film.`);
      last = { reached: false, sent, what };
    } finally {
      try { await setWeirPause(false); } catch (error) { console.log(`could not resume Weir: ${error}`); }
    }
  }
  return last ?? { reached: false, sent: undefined as unknown as Sent, what: "no attempt was made" };
}

// ------------------------------------------------------------------ what must be true once a resend has settled

/** The closing lines of one film read as one story (G09-Problems.ps1, Get-StoryProblems). */
export async function storyProblems(stack: Stack, title: string): Promise<string[]> {
  const problems: string[] = [];
  const lines = await activityLines(stack, title);
  const closing = lines.filter((l) => /told the processor what happened|could not tell the processor/.test(l));
  for (const c of closing.filter((l) => /imported the file/.test(l) && /will not import|will not be imported/.test(l))) {
    problems.push(`says both imported and will not import: ${c.replace(/\s+/g, " ").slice(0, 240)}`);
  }
  for (const c of closing.filter((l) => /[a-z0-9]\.\.(\s|"|\\|$)/.test(l))) {
    problems.push(`ends in a doubled full stop: ${c.replace(/\s+/g, " ").slice(0, 240)}`);
  }
  // One closing line for each hand-off (each send, by its own id): a resent release has the replaced send's one line and the new
  // send's one line, never two for one.
  const perSend = new Map<string, number>();
  for (const line of lines.filter((l) => /"category":"processing\.outcome-(reported|not-reported|superseded)"/.test(l))) {
    const id = /handoffid\W+([0-9a-f]{32})/.exec(line)?.[1] ?? "unknown";
    perSend.set(id, 1 + (perSend.get(id) ?? 0));
  }
  for (const [id, count] of perSend) if (count > 1) problems.push(`${count} closing lines for the one send ${id}`);
  return problems;
}

/** #1242: the first send's late report is settled, never refused. Weir's log and Deluno's answer log hold no HTTP 409 for it. */
export async function assertNoReport409(stack: Stack, sent: Sent, first: any, what: string) {
  const since = sent.sentAt - 2000;
  const ids = [first?.id, ...(await handoffsOf(stack, sent.film.release)).map((h) => h.id)].filter(Boolean) as string[];
  const weir = containerLog("weir", since).split(/\r?\n/).filter((l) => /processors\/events returned HTTP 409/.test(l));
  expect.soft(weir.length, `${what}: Weir's log shows no HTTP 409 for the hand-off report${weir.length ? `: ${weir[0].slice(0, 220)}` : ""}`).toBe(0);
  const deluno = containerLog("deluno", since).split(/\r?\n/).filter((l) => /processors\/events answered 409/.test(l) && ids.some((id) => l.includes(id)));
  expect.soft(deluno.length, `${what}: Deluno's /processors/events log shows no 409 for this film's hand-offs${deluno.length ? `: ${deluno[0].slice(0, 300)}` : ""}`).toBe(0);
}

/** Every hand-off Deluno has for a release, newest first. */
export async function handoffsOf(stack: Stack, release: string) {
  const all = await stack.deluno.must<any[]>("GET", "/api/integrations/processors/handoffs?take=200");
  return all.filter((h) => String(h.releaseName).includes(release)).sort((a, b) => String(b.createdUtc).localeCompare(String(a.createdUtc)));
}

export interface SettledOptions { sameHashMayCountAsImported?: boolean; firstWasImported?: boolean; timeoutSec?: number }

/** What must be true once a resend has settled (Assert-ResendSettled). Every check is made, and all are reported. */
export async function assertResendSettled(stack: Stack, sent: Sent, first: any, what: string, options: SettledOptions = {}) {
  const { film } = sent;
  const secondOf = async () => (await handoffsOf(stack, film.release)).find((h) => h.id !== first.id);
  const settled = await until(`the new hand-off of ${film.release} to settle`, options.timeoutSec ?? 180, async () => {
    const second = await secondOf();
    return second && second.status === "completed" && second.importJobId ? second : undefined;
  }, 1000).catch(() => undefined);
  const second = await secondOf();
  // After: Deluno imports a cleaned film within a second or two of Weir's report, so the first send is often imported before "I don't
  // want it" lands. A torrent sent again has the same info-hash, and Deluno takes it as the download it has already imported, so no
  // second hand-off is made; that is right when the first send was imported.
  let firstImported = false;
  if (!second && options.sameHashMayCountAsImported) {
    const firstNow = (await handoffsOf(stack, film.release)).find((h) => h.id === first.id);
    firstImported = !!firstNow && firstNow.status === "completed" && !!firstNow.importJobId;
    if (firstImported) console.log(`${what}: the first send was imported before the removal landed; the resent torrent (same info-hash) counts as that import.`);
  }
  expect.soft(!!settled || firstImported, `${what}: the new hand-off settles on its own (${second ? `status ${second.status}, processor ${second.processor?.state}, output ${second.outputPath}, after ${settled?.seconds} s` : firstImported ? "the first send was imported; the same torrent counts as imported" : "there is no second hand-off"})`).toBe(true);

  const lib = await until(`${film.title} to be in the library`, 60, () => importedFile(film.title), 1000).catch(() => undefined);
  expect.soft(!!lib, `${what}: the film is in the library`).toBe(true);
  const copies = (await filesUnder("media", film.title)).filter((f) => /[.](mkv|mp4)$/i.test(f.path));
  expect.soft(copies.length, `${what}: exactly one copy is in the library (${copies.length} files)`).toBe(1);

  // The import that follows a settled hand-off is given a minute to clear Weir's output: a hand-off with an import job is not yet an import.
  const cleared = await until(`Weir's output to hold nothing of ${film.title}`, 60, async () => (await filesUnder("weir", film.title)).length === 0, 1000).catch(() => undefined);
  const left = await filesUnder("weir", film.title);
  expect.soft(!!cleared && left.length === 0, `${what}: nothing of it waits in Weir's output (${left.map((f) => f.path).join("; ")})`).toBe(true);

  // A resend reuses the film's hand-off row under a new id, so the first send's id is no longer listed once the new send exists: that is
  // the first send closed. Left open means it is still listed in an open state.
  const firstNow = (await handoffsOf(stack, film.release)).find((h) => h.id === first.id);
  expect.soft(!firstNow || !["submitted", "waiting", "accepted", "started"].includes(firstNow.status), `${what}: the first send is closed, not left open (${firstNow?.status ?? "replaced by the new send"})`).toBe(true);

  const lines = await activityLines(stack, film.title);
  const unmatched = lines.filter((l) => /unmatched in processor output/.test(l));
  expect.soft(unmatched.length, `${what}: no 'unmatched in processor output' problem was raised (${unmatched.length} rows)`).toBe(0);
  const imports = lines.filter((l) => /imported into the library/.test(l));
  // When the first send was imported before the removal landed, a resend with a new download id (Usenet) is a second, real import that
  // replaces the first copy through the recycle bin (#1254): two rows, one file. Otherwise exactly one.
  const expected = options.firstWasImported && second ? 2 : 1;
  expect.soft(imports.length, `${what}: it was imported ${expected === 2 ? "twice (the first send, then the resend replacing it)" : "once"} (${imports.length} 'Imported into the library' rows)`).toBe(expected);

  const story = await storyProblems(stack, film.title);
  expect.soft(story, `${what}: the closing lines tell one story (no imported-and-will-not-import, no doubled full stop, one line per hand-off)`).toEqual([]);
  await assertNoReport409(stack, sent, first, what);
}

/** After a removal: whether the first send reached the library before it landed (Deluno imports a cleaned film within a second or two). */
export async function wasFirstImported(sent: Sent): Promise<boolean> {
  await sleep(1000);
  return !!(await until("the first send in the library", 5, () => importedFile(sent.film.title), 500).catch(() => undefined));
}

/** Weir's own answer for one hand-off (its intake status route, which only the secret Deluno shares with Weir opens), or undefined. */
export async function weirIntakeStatus(stack: Stack, handoffId: string): Promise<any | undefined> {
  if (!stack.weirSecret) return undefined;
  try {
    const response = await fetch(`${URLS.weir}/api/v1/intake/handoffs/deluno/${handoffId}`, { headers: { "X-Webhook-Secret": stack.weirSecret }, signal: AbortSignal.timeout(15_000) });
    return response.ok ? await response.json() : undefined;
  } catch { return undefined; }
}

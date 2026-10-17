import type { Browser, Page } from "@playwright/test";
import { isUsenet, type ClientKind } from "./clients.ts";
import {
  URLS, WeirApi, clientFinished, containerOf, docker, filesUnder, makeFilm, routeLibraryTo, settledLibraryFile, until, type FilmOptions,
  type FixtureFilm, type Stack
} from "./stack.ts";

/**
 * What the groups after `films` share (scripts/golden-path/suite/vm/scenarios/Common.ps1): sending a film to one client as
 * "Send anyway" on the title's page does, finding its hand-off, reading Activity, asking a client whether it still holds the
 * film, stopping and starting Weir's container, and opening Deluno's screens in a real browser.
 */

export interface Sent { kind: ClientKind; film: FixtureFilm; movieId: string; sentAt: number; layout: "folder" | "file" }

/** Adds the film to Deluno and sends its release to the one client, as the VM's Send-Film does. */
export async function sendFilm(
  stack: Stack, kind: ClientKind, label: string, layout: "folder" | "file" = "folder", options: FilmOptions = {}, via?: { id: string; name: string }
): Promise<Sent> {
  const title = `E2e${label}${Date.now().toString(36)}${kind[0].toUpperCase()}${kind.slice(1)}${layout === "file" ? "File" : ""}`;
  const usenet = isUsenet(kind);
  const film = await makeFilm(title, false, usenet, usenet ? "folder" : layout, options);
  const movie = await stack.deluno.must<any>("POST", "/api/movies", { title: film.title, releaseYear: film.year, imdbId: null, monitored: true });
  await routeLibraryTo(stack, kind);
  const sentAt = Date.now();
  await stack.deluno.must("POST", `/api/movies/${movie.id}/grab`, {
    releaseName: film.release, indexerId: via?.id ?? (usenet ? stack.usenetIndexerId : stack.indexerId),
    indexerName: via?.name ?? (usenet ? "E2E Usenet feed" : "E2E feed"), candidateQuality: "WEB 1080p",
    downloadUrl: film.url, sizeBytes: film.size, seeders: usenet ? 0 : 50, force: true, overrideReason: "end-to-end suite", duplicateAction: null
  });
  return { kind, film, movieId: movie.id, sentAt, layout: usenet ? "folder" : layout };
}

/** The newest hand-off Deluno has for a release, or undefined. */
export async function handoffOf(stack: Stack, release: string) {
  const all = await stack.deluno.must<any[]>("GET", "/api/integrations/processors/handoffs?take=200");
  return all.filter((h) => String(h.releaseName).includes(release)).sort((a, b) => String(b.createdUtc).localeCompare(String(a.createdUtc)))[0];
}

/** The film's file in the library, once it is whole (Deluno copies to a .tmp beside the final name first). */
export async function importedFile(title: string) {
  return (await filesUnder("media", title)).find((f) => !/[.]tmp$/i.test(f.path) && /[.](mkv|mp4|avi)$/i.test(f.path));
}

/** Waits for the client to finish, Weir to clean and the hand-off to settle, and the film to be in the library. */
export async function waitImported(stack: Stack, sent: Sent, seconds = 8 * 60) {
  const { film, kind } = sent;
  await until(`${kind} to finish ${film.release}`, seconds, () => clientFinished(stack, kind, film), 2000);
  await until(`Deluno to hand ${film.release} to Weir`, 120, () => handoffOf(stack, film.release), 1000);
  const settled = await until(`the hand-off of ${film.release} to settle`, seconds, async () => {
    const h = await handoffOf(stack, film.release);
    return h && h.status === "completed" && h.importJobId ? h : undefined;
  }, 1000);
  const file = await until(`${film.release} to be in the library`, 120, () => settledLibraryFile(stack, film.title), 1000);
  return { handoff: settled.value, file: file.value };
}

/** Every Activity line (an item's whole JSON, lower case) that names the film. */
export async function activityLines(stack: Stack, title: string): Promise<string[]> {
  const lines: string[] = [];
  let token: string | undefined;
  for (let page = 0; page < 6; page++) {
    const path = `/api/activity?pageSize=500${token ? `&pageToken=${encodeURIComponent(token)}` : ""}`;
    const reply = await stack.deluno.must<any>("GET", path);
    for (const item of (Array.isArray(reply) ? reply : reply.items) as any[]) lines.push(JSON.stringify(item).toLowerCase());
    token = Array.isArray(reply) ? undefined : reply.nextPageToken;
    if (!token) break;
  }
  return lines.filter((l) => l.includes(title.toLowerCase()));
}

/** The client's OWN word on whether it still has the film (in its queue or its history), never Deluno's. */
export async function clientHolds(stack: Stack, kind: ClientKind, film: FixtureFilm): Promise<boolean> {
  switch (kind) {
    case "qbittorrent": return !!await stack.qbittorrent.torrent(film.infohash);
    case "transmission": return !!await stack.transmission.torrent(film.infohash);
    case "deluge": return !!await stack.deluge.torrent(film.infohash);
    case "sabnzbd": {
      if (await stack.sabnzbd.historyJob(film.release)) return true;
      const queue = await stack.sabnzbd.call("queue");
      return (queue.queue?.slots as any[] | undefined)?.some((s) => s.filename === film.release) ?? false;
    }
    case "nzbget": {
      if (await stack.nzbget.historyJob(film.release)) return true;
      const groups = await stack.nzbget.call("listgroups") as Array<{ NZBName: string }>;
      return groups.some((g) => g.NZBName === film.release);
    }
  }
}

/** Whatever of the film is under the clients' completed folders (the incomplete folder is not a copy of the finished download). */
export async function completedLeftovers(title: string) {
  return (await filesUnder("downloads", title)).filter((f) => f.path.startsWith("/downloads/completed/"));
}

// ------------------------------------------------------------------ Weir, as a container

/** Stops Weir's container, as quitting Weir does on the VM. Returns the moment it was gone. */
export function stopWeir(): number {
  docker("stop", "--time", "20", containerOf("weir"));
  return Date.now();
}

export function startWeir() {
  docker("start", containerOf("weir"));
}

export async function weirAnswers(): Promise<boolean> {
  try { return (await fetch(`${URLS.weir}/ready`, { signal: AbortSignal.timeout(4000) })).status === 200; } catch { return false; }
}

/** Starts Weir if it is down and waits for it to answer, so the next test starts with Weir up. */
export async function restoreWeir() {
  if (await weirAnswers()) return;
  try { startWeir(); } catch { /* already starting */ }
  await until("Weir to answer again", 120, weirAnswers, 1000);
}

/** Signs in to Weir again (its session may not outlive a restart). */
export const weirSignIn = () => WeirApi.signIn();

// ------------------------------------------------------------------ a real browser on Deluno's screens

/** A page signed in as the stack's person, at the path, the way the VM's Open-DelunoPage does. */
export async function openDeluno(browser: Browser, stack: Stack, path: string, viewport = { width: 1440, height: 900 }): Promise<Page> {
  const context = await browser.newContext({ baseURL: URLS.deluno, viewport });
  const page = await context.newPage();
  await page.goto("/");
  const { accessToken, user, expiresUtc } = stack.deluno.session;
  await page.evaluate(([token, who, expires]) => {
    localStorage.setItem("deluno-auth-token", token as string);
    localStorage.setItem("deluno-auth-user", JSON.stringify(who ?? {}));
    if (expires) localStorage.setItem("deluno-auth-expires", expires as string);
  }, [accessToken, user, expiresUtc]);
  await page.goto(path);
  return page;
}

/** The visible text of the open drawer or dialog, or of the whole page when there is none. */
export function drawerText(page: Page): Promise<string> {
  return page.evaluate(() => {
    const d = document.querySelector("[role=dialog],[data-state=open][role=dialog],aside[aria-label]");
    return ((d ?? document.body) as HTMLElement).innerText;
  });
}

export const pageText = (page: Page) => page.evaluate(() => document.body?.innerText ?? "");

export const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** Weir's OWN record of a film's file (its status and what it handed back), or undefined. */
export async function weirFile(title: string) {
  const weir = await WeirApi.signIn();
  try {
    const reply = await weir.get<{ files: any[] }>("/api/v1/processing/files");
    return reply.body.files?.find((f) => String(f.relative_path).includes(title));
  } finally { await weir.dispose(); }
}

// ------------------------------------------------------------------ what Deluno shows of the clients' downloads

/**
 * Every client's queue rows as Deluno reads them, flattened (GET telemetry). With `history` the clients' finished rows come too,
 * each with the status of its outcome (a torrent whose files were deleted shows up there), as the VM's Get-TelemetryRows does.
 */
export async function telemetryRows(stack: Stack, history = false): Promise<any[]> {
  const overview = await stack.deluno.must<any>("GET", "/api/download-clients/telemetry");
  const rows: any[] = [];
  for (const snap of overview.clients ?? []) {
    for (const q of snap.queue ?? []) if (q) rows.push({ ...q, clientId: snap.clientId, clientProtocol: snap.protocol });
    if (history) for (const h of snap.history ?? []) if (h) rows.push({ ...h, status: String(h.outcome), progress: 100, isHistory: true, clientProtocol: snap.protocol });
  }
  return rows;
}

/** Deletes what a finished download left under the clients' completed folders, as a person at the file manager would. Returns how many files went. */
export async function deleteCompletedFiles(title: string): Promise<number> {
  const before = (await completedLeftovers(title)).length;
  // Deluno's container has the downloads folder writable; the files are removed by their path, deepest first.
  docker("exec", containerOf("deluno"), "sh", "-c", `find /downloads/completed -depth -path '*${title}*' -exec rm -rf {} +`);
  return before;
}

/** Every Activity line (an item's whole JSON, as the person reads it) that names the film, following the pages. */
export async function activityRaw(stack: Stack, title: string): Promise<string[]> {
  const lines: string[] = [];
  let token: string | undefined;
  for (let page = 0; page < 6; page++) {
    const reply = await stack.deluno.must<any>("GET", `/api/activity?pageSize=500${token ? `&pageToken=${encodeURIComponent(token)}` : ""}`);
    for (const item of (Array.isArray(reply) ? reply : reply.items) as any[]) lines.push(JSON.stringify(item));
    token = Array.isArray(reply) ? undefined : reply.nextPageToken;
    if (!token) break;
  }
  return lines.filter((l) => l.toLowerCase().includes(title.toLowerCase()));
}

/** Automation (the jobs that hand a finished download on) paused or running, as the Dashboard's switch does. */
export const setAutomation = (stack: Stack, on: boolean) => stack.deluno.must("PUT", "/api/settings/automation", { isEnabled: on });

/** Makes folders on the shared volumes, as a person does at the file manager before pointing a library at them (Deluno asks for the folder to exist). */
export function makeFolders(...folders: string[]) {
  docker("exec", containerOf("deluno"), "mkdir", "-p", ...folders);
}

/** Activity's newest items, parsed (each item as the API gives it), following the pages. */
export async function activityItems(stack: Stack, pageSize = 300, maxPages = 1): Promise<any[]> {
  const items: any[] = [];
  let token: string | undefined;
  for (let page = 0; page < maxPages; page++) {
    const reply = await stack.deluno.must<any>("GET", `/api/activity?pageSize=${pageSize}${token ? `&pageToken=${encodeURIComponent(token)}` : ""}`);
    items.push(...((Array.isArray(reply) ? reply : reply.items) as any[]));
    token = Array.isArray(reply) ? undefined : reply.nextPageToken;
    if (!token) break;
  }
  return items;
}

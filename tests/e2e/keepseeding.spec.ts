import fs from "node:fs";
import path from "node:path";
import { expect, test } from "@playwright/test";
import { type ClientKind } from "./clients.ts";
import { activityItems, sendFilm, sleep, telemetryRows, type Sent } from "./flow.ts";
import { filesUnder, setUpStack, type Stack } from "./stack.ts";
import { readRules, restoreRules, setHealthRules } from "./stuck.ts";

/**
 * Group `keepseeding` (scripts/golden-path/suite/vm/scenarios/G10-KeepSeeding.ps1, #1240): a struck torrent from a private tracker moves
 * to a keep-seeding category instead of being deleted. For each torrent client (qBittorrent, Deluge, Transmission) two torrents are sent
 * from the stack's Torznab feed, each stuck just short of the end (its data is on disk) and struck by "No progress" at two checks in a row:
 *   - one from "Golden private feed", an indexer the group adds and marks private; the rule says "Move it to the keep-seeding category
 *     and look for another copy". Activity says it was moved, to which category, on which client, and that Deluno is looking for another
 *     copy; the client (asked itself, not Deluno) still holds it, now in the keep-seeding category; its data is still on disk; it is gone
 *     from Transfers and stays gone through another check; nothing was removed; no problem is raised;
 *   - one from the public feed, struck by the same rule: removed from the client and replaced, exactly as before.
 * The private tracker is the fixture’s feed under another address (/private/api). The group puts the rule, the keep-seeding choices and the
 * indexer back at the end through Deluno’s own API. The category made in qBittorrent and Deluge stays in the client (Deluno never deletes one).
 */
test.describe.configure({ mode: "serial" });

const TORRENTS: ClientKind[] = ["qbittorrent", "deluge", "transmission"];
const CATEGORY = "golden-keep-seeding";
const PRIVATE_NAME = "Golden private feed";

let stack: Stack;
let originalRules: Awaited<ReturnType<typeof readRules>> = [];
let originalView: any;
let privateIndexerId: string | undefined;
let choicesChanged = false;
const ready = new Map<ClientKind, any>();
const films = new Map<string, Sent>();
const seen = new Map<string, { message?: string; seconds?: number }>();
let lines: any[] = [];
let afterView: any;

/** The keep-seeding category in the client, made as a person does in the client first (Deluno never creates one). Transmission’s labels need no making. */
async function makeClientCategory(kind: ClientKind) {
  if (kind === "qbittorrent") {
    const made = await stack.qbittorrent.post("/api/v2/torrents/createCategory", { category: CATEGORY, savePath: "" });
    // 409 is "Unable to create category": it is already there from an earlier run.
    if (made.status !== 200 && made.status !== 409) throw new Error(`qBittorrent would not make the category: ${made.status} ${made.text}`);
  } else if (kind === "deluge") {
    try { await stack.deluge.call("label.add", [CATEGORY]); } catch (error) { if (!/already exists/i.test(String(error))) throw error; }
  }
}

/** What the client itself says about one torrent: whether it holds it, and its category or label. Never asks Deluno. */
async function clientTorrent(kind: ClientKind, hash: string): Promise<{ present: boolean; category: string | null }> {
  switch (kind) {
    case "qbittorrent": { const t = await stack.qbittorrent.torrent(hash); return { present: !!t, category: t ? t.category : null }; }
    case "deluge": { const label = await stack.deluge.label(hash); return { present: label !== undefined, category: label ?? null }; }
    default: { const labels = await stack.transmission.labels(hash); return { present: !!labels, category: labels ? labels.join(",") : null }; }
  }
}

/** The strike lines Deluno wrote for one release: Activity rows of category download.health.remediated whose details name it. */
function strikeLines(film: Sent["film"]) {
  const out: Array<{ message: string; actions: string[]; movedTo: string }> = [];
  for (const row of lines) {
    if (row.category !== "download.health.remediated" || !JSON.stringify(row).includes(film.release)) continue;
    let details: any;
    try { details = JSON.parse(String(row.detailsJson)); } catch { continue; }
    if (!String(details?.releaseName ?? "").toLowerCase().startsWith(film.release.toLowerCase())) continue;
    out.push({ message: String(row.message), actions: ((details.actions ?? []) as unknown[]).map(String), movedTo: String(details.movedTo ?? "") });
  }
  return out;
}

/** The "Moved X to Y on Z so it keeps seeding on T, and is looking for another copy" message for a release. */
const movedMessage = (film: Sent["film"], rows: any[]) => rows.map((r) => ({ r, m: String(r.message ?? "") })).find(({ r, m }) => JSON.stringify(r).includes(film.release) && /so it keeps seeding/.test(JSON.stringify(r)) && /^Moved /.test(m))?.m;

/** Whether a strike removed the release from the client (the public torrent’s story, and what a private torrent must never have). */
function removedMessage(film: Sent["film"], rows: any[]) {
  const saved = lines; lines = rows;
  try { return strikeLines(film).find((s) => s.actions.some((a) => /removed/.test(a)))?.message; } finally { lines = saved; }
}

function saveEvidence(name: string, text: string) {
  const dir = path.resolve(process.cwd(), "artifacts/e2e/evidence");
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, name), text);
}

test.beforeAll(async () => {
  test.setTimeout(25 * 60_000);
  stack = await setUpStack();
  originalRules = await readRules(stack);
  originalView = await stack.deluno.must("GET", "/api/download-health/keep-seeding");

  // ---------------- setup: a private indexer, the category in each client, Deluno told about it, and the rule
  const priv = await stack.deluno.must<any>("POST", "/api/indexers", {
    name: PRIVATE_NAME, protocol: "torznab", privacy: "private", baseUrl: "http://fixture:8765/private", apiKey: "golden",
    priority: 26, categories: "2000,2040,5000,5040", mediaScope: "both", isEnabled: true, keepName: true, routeToLibraries: true,
    // Nothing automatic (#936): the group sends each release itself.
    rssEnabled: false, automaticSearchEnabled: false
  });
  privateIndexerId = priv.id;
  console.log(`Private indexer ${PRIVATE_NAME} added; Deluno reads it as private: ${priv.privacy}`);
  const clientCategories: Record<string, string> = {};
  for (const kind of TORRENTS) { await makeClientCategory(kind); clientCategories[stack.clientIds[kind]] = CATEGORY; }
  choicesChanged = true;
  await stack.deluno.must("PUT", "/api/download-health/keep-seeding", { clientCategories, indexers: {}, tagCategories: {} });
  for (const kind of TORRENTS) ready.set(kind, await stack.deluno.must("POST", "/api/download-health/keep-seeding/check", { clientId: stack.clientIds[kind], category: CATEGORY }));

  await setHealthRules(stack, { "no-progress": { strikes: 2 } }, 1);
  const rule = (await readRules(stack)).find((r) => r.kind === "no-progress")!;
  await stack.deluno.must("PUT", `/api/download-health/rules/${rule.id}`, { ...rule, privateAction: "move-and-replace", blocklistWhenMoved: true });

  // ---------------- send both kinds of torrent to each client
  for (const kind of TORRENTS) {
    films.set(`private.${kind}`, await sendFilm(stack, kind, "keepprivate", "file", { variant: "short" }, { id: privateIndexerId!, name: PRIVATE_NAME }));
  }
  for (const kind of TORRENTS) films.set(`public.${kind}`, await sendFilm(stack, kind, "keeppublic", "file", { variant: "short" }));

  // Waits until every film has been dealt with by a strike: the private ones moved, the public ones removed (or the time runs out).
  for (const key of films.keys()) seen.set(key, {});
  const started = Date.now();
  while (Date.now() - started < 480_000) {
    const rows = await activityItems(stack, 300, 1).catch(() => [] as any[]);
    for (const [key, sent] of films) {
      const s = seen.get(key)!;
      if (s.message) continue;
      const m = key.startsWith("private.") ? movedMessage(sent.film, rows) : removedMessage(sent.film, rows);
      if (m) { s.message = m; s.seconds = Math.round((Date.now() - sent.sentAt) / 1000); }
    }
    if ([...seen.values()].every((s) => s.message)) break;
    await sleep(4000);
  }
  // One more real check interval, so "stays gone" and "is not struck again" are read after a further pass, not at the moment of the move.
  await sleep(75_000);
  lines = await activityItems(stack, 500, 2);
  afterView = await stack.deluno.must("GET", "/api/download-health/keep-seeding");
});

test.afterAll(async () => {
  if (!stack) return;
  // Put everything back through Deluno’s own API: the rules, the keep-seeding choices, and the private indexer.
  await restoreRules(stack, originalRules);
  if (choicesChanged) {
    const before: Record<string, string> = {};
    for (const c of (originalView?.clients ?? []) as any[]) if (c.category) before[c.clientId] = c.category;
    await stack.deluno.call("PUT", "/api/download-health/keep-seeding", { clientCategories: before, indexers: {}, tagCategories: {} });
  }
  if (privateIndexerId) await stack.deluno.call("DELETE", `/api/indexers/${privateIndexerId}`);
});

for (const kind of TORRENTS) {
  test(`keepseeding.moved.${kind}: A struck ${kind} torrent from a private tracker is moved to the keep-seeding category, not deleted`, async () => {
    const pf = films.get(`private.${kind}`)!.film;
    const clientName = (await stack.deluno.must<any[]>("GET", "/api/download-clients")).find((c) => c.id === stack.clientIds[kind])?.name ?? "";
    expect.soft(ready.get(kind)?.status, `Deluno sees the keep-seeding category ready in ${kind}: ${ready.get(kind)?.message}`).toBe("ready");
    const message = seen.get(`private.${kind}`)?.message;
    expect.soft(!!message, `Activity says it was moved (${message ? `after ${seen.get(`private.${kind}`)!.seconds} s` : 'no "Moved ... so it keeps seeding" line in Activity'})`).toBe(true);
    if (message) {
      saveEvidence(`keepseeding-moved-${kind}.txt`, message);
      expect.soft(message.includes(pf.release) && message.includes(` to ${CATEGORY} on `) && message.includes(` on ${clientName} `), `The line names the release, the category and the client: ${message}`).toBe(true);
      expect.soft(message, "The line names the private tracker").toContain(PRIVATE_NAME);
      expect.soft(message, "Activity says plainly that Deluno is looking for another copy").toMatch(/looking for another copy/);
      expect.soft(message, "The line gives the strike rule’s own reason (No progress)").toMatch(/No progress|made no progress/);
    }
    expect.soft(removedMessage(pf, lines), "Activity never says it was removed from the client").toBeUndefined();

    const held = await clientTorrent(kind, pf.infohash);
    expect.soft(held.present, `${kind} still holds the torrent (it was not deleted)`).toBe(true);
    expect.soft(held.present && held.category?.toLowerCase(), `${kind} reports it in the keep-seeding category (${CATEGORY}); it says '${held.category}'`).toBe(CATEGORY);

    const files = await filesUnder("downloads", pf.release);
    expect.soft(files.filter((f) => f.size > 0).length, `Its data is still on disk (${files.slice(0, 3).map((f) => `${f.path} (${f.size} bytes)`).join("; ")})`).toBeGreaterThan(0);

    const row = (await telemetryRows(stack)).find((r) => String(r.releaseName ?? "").includes(pf.release) || String(r.title ?? "").includes(pf.release));
    expect.soft(row, `It is gone from Transfers, and stayed gone through another check${row ? `: still listed as '${row.status}'` : ""}`).toBeUndefined();
    const strikes = strikeLines(pf);
    expect.soft(strikes.length, `It was not struck again after the move (one strike line, the move): ${strikes.length} line(s): ${strikes.map((s) => s.actions.join("+")).join(" | ")}`).toBe(1);
    const problems = ((afterView?.problems ?? []) as any[]).filter((p) => String(p.releaseName ?? "").includes(pf.title));
    expect.soft(problems.map((p) => p.message), "No keep-seeding problem is raised for it").toEqual([]);
  });

  test(`keepseeding.public-removed.${kind}: A struck ${kind} torrent from a public tracker is removed and replaced, as before`, async () => {
    const pub = films.get(`public.${kind}`)!.film;
    const message = seen.get(`public.${kind}`)?.message;
    expect.soft(!!message, `Activity says it was struck and replaced (${message ? `after ${seen.get(`public.${kind}`)!.seconds} s` : 'no "Fixed a stuck download" line in Activity'})`).toBe(true);
    if (message) {
      saveEvidence(`keepseeding-public-${kind}.txt`, message);
      expect.soft(/looking for another copy/.test(message) && /removed it from/.test(message), `Deluno removed it from the client and looked for another copy: ${message}`).toBe(true);
      expect.soft(movedMessage(pub, lines), "It was not moved to the keep-seeding category").toBeUndefined();
    }
    const start = Date.now();
    let present = true;
    while (Date.now() - start < 60_000) { present = (await clientTorrent(kind, pub.infohash)).present; if (!present) break; await sleep(3000); }
    expect.soft(present, `${kind} no longer holds it`).toBe(false);
  });
}

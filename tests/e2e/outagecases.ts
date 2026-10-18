import { expect, test, type Page } from "@playwright/test";
import {
  handoffOf, openDeluno, pageText, restoreWeir, sendFilm, sleep, startWeir, stopWeir, weirAnswers, weirFile, weirSignIn,
  waitImported, type Sent
} from "./flow.ts";
import type { ClientKind } from "./clients.ts";
import { clientFinished, setUpStack, until, type Stack } from "./stack.ts";
import { setUpTvStack, type Media } from "./tv.ts";

/**
 * Group `outage` (scripts/golden-path/suite/vm/scenarios/G06-Outage.ps1, #1213, #1227): Weir stops with nothing in flight and
 * with a hand-off in flight; System, the Dashboard and Deluno's own record of the Weir connection go amber within a minute and
 * come back within a minute. Weir's container is stopped and started; the two screens are read in a real browser every 300 ms
 * (the text a person reads); the record is Deluno's own processor connection.
 *
 * The VM also reads the Windows tray icon (its hover text and dot) in the same watch. A tray exists only on Windows, so those
 * readings belong to the `windows` job (the parity table's `outage.tray`); everything else is made here.
 */
/** Registers the Weir outage scenarios of a media type on one stack made for the spec file. Call it at the top level of a spec. */
export function defineOutage(media: Media) {
  test.describe.configure({ mode: "serial" });

  let stack: Stack;
  let dashboard: Page;
  let system: Page;

  interface Signals {
    at: number; systemAmber: boolean; systemGreen: boolean; dashboardAmber: boolean; dashboardGreen: boolean; serverHealth: string | null;
  }

  /** What the places say right now about Weir. */
  async function readSignals(): Promise<Signals> {
    const sys = await pageText(system).catch(() => "");
    const dash = await pageText(dashboard).catch(() => "");
    let health: string | null = null;
    try {
      const conns = await stack.deluno.must<any[]>("GET", "/api/integrations/processors/connections");
      health = String(conns.find((c) => /weir/i.test(c.name))?.healthStatus ?? "");
    } catch { /* Deluno's answer is read again next time */ }
    return {
      at: Date.now(),
      systemAmber: /PROCESSORS[\s\S]{0,40}?0\/1/.test(sys),
      systemGreen: /PROCESSORS[\s\S]{0,40}?1\/1/.test(sys),
      dashboardAmber: /\d+ not answering|Processor\s*·?\s*not answering|Weir[^\n]{0,60}not answering/.test(dash),
      dashboardGreen: /all answering/.test(dash) && !/not answering/.test(dash),
      serverHealth: health
    };
  }

  /** Reads until each place has been seen amber (or green), or the time runs out. Seconds since T0 per place, or null. */
  async function watch(want: "amber" | "green", t0: number, timeoutSec = 120) {
    const first: Record<"system" | "dashboard" | "server", number | null> = { system: null, dashboard: null, server: null };
    while ((Date.now() - t0) / 1000 < timeoutSec) {
      const r = await readSignals();
      const since = (r.at - t0) / 1000;
      const hit = {
        system: want === "amber" ? r.systemAmber : r.systemGreen,
        dashboard: want === "amber" ? r.dashboardAmber : r.dashboardGreen,
        server: want === "amber" ? /degraded|unhealthy|down|unreachable/i.test(r.serverHealth ?? "") : /healthy/i.test(r.serverHealth ?? "") && !/un/i.test(r.serverHealth ?? "")
      };
      for (const key of Object.keys(first) as Array<keyof typeof first>) if (first[key] === null && hit[key]) first[key] = Math.round(since * 10) / 10;
      if (Object.values(first).every((v) => v !== null)) break;
      await sleep(300);
    }
    return first;
  }

  function within(name: string, seconds: number | null, limit = 60) {
    expect(seconds !== null && seconds <= limit, `${name} (${seconds !== null ? `after ${seconds} s` : "never within the watch time"}, limit ${limit} s)`).toBe(true);
  }

  async function outageOnce(label: string) {
    const before = await readSignals();
    expect(before.systemGreen && before.dashboardGreen, `${label}: before the outage every place reads green (system green ${before.systemGreen}; dashboard green ${before.dashboardGreen})`).toBe(true);
    const t0 = stopWeir();
    console.log(`${label}: Weir stopped at T0`);
    const amber = await watch("amber", t0);
    within(`${label}: System goes amber within a minute`, amber.system);
    within(`${label}: the Dashboard goes amber within a minute`, amber.dashboard);
    within(`${label}: Deluno's own record of the Weir connection goes amber within a minute`, amber.server);
  }

  async function completeOutage(label: string) {
    startWeir();
    const startedAt = Date.now();
    const up = await until("Weir to answer again", 90, weirAnswers, 500);
    console.log(`${label}: Weir answers again after ${up.seconds} s`);
    // "Within a minute" is counted from the moment Weir answered.
    const t1 = startedAt + up.seconds * 1000;
    const green = await watch("green", t1);
    within(`${label}: System comes back within a minute of Weir answering`, green.system);
    within(`${label}: the Dashboard comes back within a minute`, green.dashboard);
    within(`${label}: Deluno's own record comes back within a minute`, green.server);
  }

  test.beforeAll(async ({ browser }) => {
    test.setTimeout(5 * 60_000);
    stack = media === "tv" ? await setUpTvStack() : await setUpStack();
    dashboard = await openDeluno(browser, stack, "/");
    system = await openDeluno(browser, stack, "/system");
    await sleep(3000);
  });

  test.afterAll(async () => {
    await restoreWeir();
    await dashboard?.context().close();
    await system?.context().close();
  });

  if (media === "movies") test("outage.idle: Weir stops with nothing in flight: System and the Dashboard go amber within a minute and come back", async () => {
    test.setTimeout(10 * 60_000);
    try {
      await outageOnce("idle");
      await completeOutage("idle");
    } finally { await restoreWeir(); }
  });

  for (const kind of (media === "tv" ? ["qbittorrent", "sabnzbd"] : ["qbittorrent"]) as ClientKind[]) test(media === "tv" ? `tv.outage.inflight.${kind}: Weir stops with a TV episode's hand-off in flight: the same places go amber and come back, and the hand-off ends clearly` : "outage.inflight: Weir stops with a hand-off in flight: the same places go amber and come back, and the hand-off ends clearly", async () => {
    test.setTimeout(20 * 60_000);
    // A hand-off is in flight while Weir holds it unfinished. A film is cleaned in seconds, so Weir is paused: it takes the
    // hand-off and keeps it, and is stopped with it held.
    let weir = await weirSignIn();
    const sent: Sent = await sendFilmPaused(weir, kind);
    const { film } = sent;
    try {
      await until(`${kind} to finish ${film.release}`, 240, () => clientFinished(stack, kind, film), 2000);
      const inFlight = await until("a hand-off to be in flight", 180, async () => {
        const h = await handoffOf(stack, film.release);
        return h && ["waiting", "submitted", "accepted", "started"].includes(h.status) ? h : undefined;
      }, 1000).catch(async () => undefined);
      const h = await handoffOf(stack, film.release);
      expect(inFlight, `A hand-off is in flight when Weir stops (${inFlight ? `status ${inFlight.value.status}, processor said '${inFlight.value.processor?.state}'` : h ? `the hand-off was already ${h.status}` : "no hand-off was made in 3 minutes"})`).toBeTruthy();
      try {
        await outageOnce("inflight");
        await completeOutage("inflight");
      } finally { await restoreWeir(); }
    } finally {
      try { weir = await weirSignIn(); await weir.put("/api/v1/pause", { paused: false, scan_while_paused: true }); } catch (error) { console.log(`could not resume Weir: ${error}`); }
    }

    // The hand-off must end in a place a person can see: finished, or failed and offered again; never silently open for good.
    const end = await until("the in-flight hand-off to end", 300, async () => {
      const h = await handoffOf(stack, film.release);
      return h && ["completed", "failed", "rejected", "lost", "timed-out", "stalled", "cancelled"].includes(h.status) ? h : undefined;
    }, 2000).catch(() => undefined);
    expect(end, "The in-flight hand-off ended clearly after Weir came back (finished, or failed and offered again)").toBeTruthy();
    console.log(`inflight: the hand-off ended as ${end!.value.status} after ${end!.seconds} s`);
    if (end!.value.status !== "completed") {
      const retry = await stack.deluno.call("POST", `/api/integrations/processors/handoffs/${end!.value.id}/retry`);
      expect(retry.status >= 200 && retry.status < 300, `Send again works on it (HTTP ${retry.status})`).toBe(true);
      await until("the hand-off to settle again", 240, async () => {
        const h = await handoffOf(stack, film.release);
        return h?.status === "completed" && h.importJobId ? h : undefined;
      }, 1000);
      expect(await waitImported(stack, sent), "It then settles and imports").toBeTruthy();
    }
    // Weir's own record of the film agrees it finished it.
    const weirSays = await until("Weir's own record of the file", 120, () => weirFile(film.title), 2000);
    expect(["processed", "imported"], "Weir's own record of the file says it processed it").toContain(weirSays.value.status);
  });

  async function sendFilmPaused(weir: Awaited<ReturnType<typeof weirSignIn>>, kind: ClientKind): Promise<Sent> {
    const paused = await weir.put("/api/v1/pause", { paused: true, scan_while_paused: true });
    expect(paused.status, "Weir took the pause").toBeLessThan(300);
    return sendFilm(stack, kind, "Outage", "file");
  }
}

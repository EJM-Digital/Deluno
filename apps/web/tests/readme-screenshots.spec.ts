import fs from "node:fs";
import path from "node:path";
import { test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "./helpers/auth-helper";
import { serveLiveBusy } from "./helpers/live-busy";
import { serveLiveRig } from "./helpers/live-rig";
import { untilQuiet } from "./helpers/settle";

// Retakes the README's screenshots (#1122) from the same mock data the smoke
// tests use: the Dashboard, Transfers and Activity come from serveLiveRig; the
// library pages are real titles seeded into the disposable backend, with
// artwork, quality and ratings added to the answers on the way to the browser.
//
// Not part of the normal run. To retake them:
//   $env:README_SCREENSHOTS = "1"; npx playwright test tests/readme-screenshots.spec.ts --project=chromium
// then look at every image: none may show a private address, host, path, key or username.

const outDir = path.resolve(process.cwd(), "../../screenshots");

const FILMS: Array<[string, number, string, string, number, string]> = [
  ["Metropolis", 1927, "Science Fiction, Drama", "Bluray 1080p", 8.3, "In a futuristic city divided between the rich who plan and the workers who toil below, the master's son crosses the line."],
  ["Nosferatu", 1922, "Horror, Fantasy", "Bluray 1080p", 7.9, "A real estate agent travels to a remote castle and brings something back with him."],
  ["The Kid", 1921, "Comedy, Drama", "Bluray 1080p", 8.2, "A tramp takes in an abandoned child, and the two make a living by mischief and love."],
  ["Sintel", 2010, "Animation, Fantasy", "WEB 1080p", 7.5, "A girl searches for the baby dragon she befriended."],
  ["Big Buck Bunny", 2008, "Animation, Comedy", "WEB 1080p", 7.2, "A giant rabbit takes revenge on three bullying rodents."],
  ["Tears of Steel", 2012, "Science Fiction", "WEB 2160p", 6.4, "A group of warriors and scientists try to save the world from robots."],
  ["Night of the Living Dead", 1968, "Horror", "Bluray 1080p", 7.8, "A group of strangers barricade themselves in a farmhouse."],
  ["Plan 9 from Outer Space", 1959, "Science Fiction", "DVD", 4.0, "Aliens resurrect the dead to stop humanity building a doomsday weapon."],
  ["Sita Sings the Blues", 2008, "Animation, Musical", "WEB 1080p", 7.3, "The Ramayana retold through the songs of a 1920s jazz singer."],
  ["The Little Shop of Horrors", 1960, "Comedy, Horror", "Bluray 720p", 6.3, "A florist's assistant raises a plant that eats people."],
  ["Coherence", 2013, "Science Fiction, Thriller", "WEB 1080p", 7.2, "A comet passes and eight friends at a dinner party find reality coming apart."],
  ["Primer", 2004, "Science Fiction", "WEB 1080p", 6.8, "Two engineers stumble on something they cannot explain."],
  ["Moon", 2009, "Science Fiction", "Bluray 1080p", 7.8, "A lone astronaut nearing the end of his contract on the Moon finds he is not alone."],
  ["Her", 2013, "Romance, Science Fiction", "WEB 2160p", 7.9, "A lonely writer falls for his operating system."],
  ["Arrival", 2016, "Science Fiction, Drama", "WEB 1080p", 7.9, "A linguist is recruited to talk to visitors from another world."],
  ["Past Lives", 2023, "Romance, Drama", "WEB 1080p", 7.8, "Two childhood friends reunite after decades apart."]
];
const MISSING = new Set(["Plan 9 from Outer Space", "The Little Shop of Horrors", "Past Lives"]);

function poster(title: string, year: number, n: number) {
  const hue = (n * 47) % 360;
  const lines: string[] = [];
  let line = "";
  for (const word of title.split(" ")) {
    if ((line + " " + word).trim().length > 13 && line) {
      lines.push(line);
      line = word;
    } else {
      line = (line + " " + word).trim();
    }
  }
  lines.push(line);
  const text = lines
    .slice(0, 4)
    .map((l, i) => `<text x='150' y='${210 + i * 34}' text-anchor='middle' font-family='Georgia,serif' font-size='28' font-weight='bold' fill='white'>${l}</text>`)
    .join("");
  const svg =
    `<svg xmlns='http://www.w3.org/2000/svg' width='300' height='450' viewBox='0 0 300 450'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'>` +
    `<stop offset='0' stop-color='hsl(${hue} 55% 38%)'/><stop offset='1' stop-color='hsl(${(hue + 50) % 360} 60% 16%)'/></linearGradient></defs>` +
    `<rect width='300' height='450' fill='url(#g)'/><circle cx='230' cy='80' r='46' fill='hsl(${hue} 70% 70% / .35)'/>${text}` +
    `<text x='150' y='410' text-anchor='middle' font-family='sans-serif' font-size='20' fill='white' fill-opacity='.7'>${year}</text></svg>`;
  return "data:image/svg+xml;utf8," + encodeURIComponent(svg);
}

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

function dress(item: Record<string, unknown>) {
  const index = FILMS.findIndex(([title]) => title === item.title);
  if (index < 0) return item;
  const [title, year, genres, quality, rating, overview] = FILMS[index];
  const have = !MISSING.has(title);
  const web = quality.startsWith("WEB");
  return {
    ...item,
    posterUrl: poster(title, year, index + 3),
    backdropUrl: null,
    overview,
    genres,
    rating,
    hasFile: have,
    monitored: true,
    currentQuality: have ? quality : null,
    targetQuality: "Bluray 1080p",
    fileSizeBytes: have ? Math.round((quality.includes("2160") ? 22 : quality.includes("720") ? 3.4 : 8.1) * 1e9) : null,
    wantedStatus: have ? (web ? "upgrade" : "met") : "missing",
    wantedReason: have ? (web ? "Quality upgrade" : "Quality met") : "Missing",
    qualityCutoffMet: have && !web,
    videoCodec: have ? "H.264" : null,
    audioCodec: have ? "AAC" : null,
    runtimeMinutes: 90 + index * 4,
    releaseGroup: null,
    filePath: null
  };
}

// Assembled from pieces, as scripts/check-public-exposure.ps1 does, so this guard does not itself spell out the names
// and addresses it keeps out of the screenshots.
const PRIVATE = new RegExp(
  ["10\\.1" + "\\.1\\.", "C:\\\\Users\\\\", "SPARE" + "-PC", "WIN" + "-", "storage" + "-city"].join("|"),
  "i"
);

/** Every string a person could read in the picture: the text, the title, aria labels, tooltips, placeholders and field values. */
async function assertNothingPrivate(page: Page, name: string) {
  const seen = await page.evaluate(() => {
    const out: string[] = [document.title, document.body.innerText];
    for (const el of Array.from(document.querySelectorAll("*"))) {
      for (const attribute of ["aria-label", "title", "placeholder", "alt", "aria-description", "href"]) {
        const value = el.getAttribute(attribute);
        if (value) out.push(value);
      }
      if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) out.push(el.value);
    }
    return out;
  });
  const found = seen.filter((text) => PRIVATE.test(text)).map((text) => text.slice(0, 120));
  if (found.length > 0) throw new Error(`${name}: private details would show in the screenshot: ${JSON.stringify(found)}`);
}

test.describe("README screenshots", () => {
  test.skip(!process.env.README_SCREENSHOTS, "Only run to retake the README screenshots.");

  test("retake", async ({ page }, info) => {
    test.skip(info.project.name !== "chromium", "Desktop only.");
    test.setTimeout(240_000);
    fs.mkdirSync(outDir, { recursive: true });
    await page.setViewportSize({ width: 1920, height: 1080 });
    // Whatever the disposable backend says about the machine it runs on is scrubbed on the way to the
    // browser (addresses, the data folder, the data-protection key), so the screenshots never show it.
    // Registered first, so the mocks below take precedence over it.
    await page.route(/\/api\//, async (route) => {
      try {
        const response = await route.fetch();
        if (!(response.headers()["content-type"] ?? "").includes("json")) return route.fulfill({ response });
        const text = (await response.text())
          .replace(/\b(?!127\.0\.0\.1\b|0\.0\.0\.0\b)(?:\d{1,3}\.){3}(\d{1,3})\b(?=[^.\d]|$)/g, "192.168.1.$1")
          .replace(/[A-Za-z]:\\\\Users\\\\[^"]*/g, "D:\\\\Deluno\\\\data")
          .replace(/Key \{[0-9a-f-]+\}/gi, "Key {…}");
        return route.fulfill({ response, body: text });
      } catch {
        return route.continue();
      }
    });
    const rig = await serveLiveRig(page);
    // A finished setup, and tidy names for the rig's filler tasks.
    await page.route(/\/api\/setup\/progress/, (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ lastCompletedStep: 9, isSkipped: false, isCompleted: true, updatedUtc: new Date().toISOString() }) }));
    const taskNames = ["Back up the databases", "Check for updates", "Refresh metadata", "Clean up artwork", "Check disk space", "Read new files", "Sync subtitles", "Tidy the log"];
    (rig.summary.automation.tasks as Array<{ key: string; name: string }>).filter((task) => /^t\d$/.test(task.key)).forEach((task, n) => { task.name = taskNames[n % taskNames.length]; });

    // The library: seed real titles, then dress what the browser is told.
    await authenticateAndNavigate(page, "/movies");
    const headers = await authHeaders(page);
    const ids: Record<string, string> = {};
    for (const [title, year] of FILMS) {
      const made = (await (await page.request.post("/api/movies/", { headers, data: { title, releaseYear: year, monitored: true } })).json()) as { id: string };
      ids[title] = made.id;
    }
    // Activity is the mocked history, not the jobs the seeding above queued.
    await page.route(/\/api\/jobs(\?|$)/, (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ items: [], hasMore: false, nextPageToken: null }) }));
    // Nothing private shows: the machine is called MEDIA-SERVER and the person Alex.
    await page.route(/\/api\/system\/info/, async (route) => {
      const response = await route.fetch();
      const body = await response.json();
      await route.fulfill({ response, json: { ...body, hostName: "MEDIA-SERVER", hostLooksLikeContainerId: false } });
    });
    // The machine's readings: an ordinary home server, not this workstation. The pushed reading is switched off
    // (the live connection passes everything else on) so the page keeps asking for this one.
    await page.routeWebSocket(/\/hubs\//, (ws) => {
      const server = ws.connectToServer();
      ws.onMessage((message) => server.send(message));
      server.onMessage((message) => {
        if (typeof message === "string" && message.includes("SystemOverviewChanged")) return;
        ws.send(message);
      });
    });
    await page.route(/\/api\/system\/overview/, async (route) => {
      const response = await route.fetch();
      const body = await response.json();
      const wave = (base: number, swing: number, seed: number) => Array.from({ length: 60 }, (_, n) => Math.max(0, Math.round((base + swing * Math.sin((n + seed) / 5) + ((n * 7 + seed) % 5)) * 10) / 10));
      const GB = 1024 ** 3;
      await route.fulfill({
        response,
        json: {
          ...body,
          instance: { ...body.instance, address: "http://192.168.1.20:5099", cpuPercent: 4, memoryBytes: 231 * 1024 * 1024, startedUtc: new Date(Date.now() - 55 * 60_000).toISOString() },
          machine: { os: "Windows 11 Pro (10.0.22631)", uptimeSeconds: 3 * 86_400 + 4 * 3_600, rebootPending: null, cpuTemperatureC: null, cpuPercent: 14, cores: 8, memoryUsedBytes: 9.6 * GB, memoryTotalBytes: 32 * GB, diskReadBytesPerSec: 2.0e6, diskWriteBytesPerSec: 6.5e6, diskIops: 140 },
          network: { adapter: "Ethernet", linkMbps: 1000, ip: "192.168.1.20", inBytesPerSec: 3.1e6, outBytesPerSec: 0.4e6, internet: true, dnsLookupMs: 8, nasMs: null, nasName: null, metadataMs: 50, clockInSync: true },
          drives: [{ name: "Deluno data", path: "D:\\Deluno\\data", totalBytes: 2 * 1024 * GB, freeBytes: 1.4 * 1024 * GB, delunoBytes: 11 * GB, fullInDays: null, readBytesPerSec: 2.0e6, writeBytesPerSec: 6.5e6, iops: 140, busyPercent: 6, health: "good", healthNote: null }],
          history: { cpu: wave(12, 6, 0), memory: wave(28, 2, 3), disk: wave(8, 5, 7), netIn: wave(3, 2, 11) }
        }
      });
    });
    await page.addInitScript(() => {
      try {
        const raw = localStorage.getItem("deluno-auth-user");
        if (raw) localStorage.setItem("deluno-auth-user", JSON.stringify({ ...JSON.parse(raw), displayName: "Alex", username: "alex" }));
      } catch {
        // not signed in yet
      }
    });
    await page.route(/\/api\/movies\/page/, async (route) => {
      const response = await route.fetch();
      const body = await response.json();
      body.items = body.items.map(dress);
      await route.fulfill({ response, json: body });
    });
    await page.route(/\/api\/movies\/[^/?]+$/, async (route) => {
      if (route.request().method() !== "GET") return route.continue();
      const response = await route.fetch();
      const body = await response.json();
      await route.fulfill({ response, json: dress(body) });
    });

    const shot = async (url: string, name: string, ready: string | RegExp) => {
      await page.goto(url);
      await page.getByText(ready).first().waitFor({ timeout: 20_000 });
      await untilQuiet(page);
      await assertNothingPrivate(page, name);
      await page.screenshot({ path: path.join(outDir, `${name}.png`) });
    };

    await shot("/", "dashboard-live", /pipeline/i);
    await shot("/?tab=system", "dashboard-system", /health/i);
    // Transfers and Activity read best from the healthy busy board: things downloading, queued and imported.
    const busy = await serveLiveBusy(page);
    const { json, at, host, queue, events } = busy as unknown as {
      json: (b: unknown) => never;
      at: (ms: number) => string;
      host: string;
      queue: Array<{ status: string }>;
      events: Array<{ category: string }>;
    };
    const capabilities = { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "password" };
    const live = queue.filter((item) => item.status !== "stalled");
    const summaryFor = (items: Array<{ status: string }>, speed: number, up: number, seeding: number) => ({
      activeCount: items.filter((item) => item.status === "downloading").length,
      queuedCount: items.filter((item) => item.status === "queued").length,
      completedCount: 0,
      stalledCount: 0,
      processingCount: items.filter((item) => item.status === "waitingForProcessor").length,
      importReadyCount: items.filter((item) => item.status === "importReady").length,
      totalSpeedMbps: speed,
      totalUploadSpeedMbps: up,
      waitingForProcessorCount: items.filter((item) => item.status === "waitingForProcessor").length,
      seedingCount: seeding
    });
    const snapshot = (clientId: string, clientName: string, protocol: string, items: Array<{ status: string }>, speed: number, up: number, seeding: number) => ({
      clientId, clientName, protocol, endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null, capabilities,
      summary: summaryFor(items, speed, up, seeding), queue: items, history: [], capturedUtc: at(-30_000), historyTruncated: false,
      clientPaused: false, clientVersion: null, clientWarnings: null
    });
    await page.route(/\/api\/download-clients\/telemetry/, (route) =>
      route.fulfill(json({
        summary: summaryFor(live, 14.2, 1.3, 14),
        clients: [snapshot("c1", `Deluge${host}`, "torrent", live, 14.2, 1.3, 14), snapshot("c2", `SABnzbd${host}`, "usenet", [], 0, 0, 0)],
        capturedUtc: at(-30_000)
      })));
    // Activity: what mattered, not the routine housekeeping.
    const mattered = events.filter((event) => !/^(job\.|metadata\.artwork)/.test(event.category));
    await page.route(/\/api\/activity(\?|$)/, (route) => route.fulfill(json({ items: mattered, hasMore: false, nextPageToken: null })));
    const noCases = { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] };
    await page.route(/\/api\/(movies|series)\/import-recovery/, (route) => route.fulfill(json(noCases)));

    await shot("/movies", "movies", "Metropolis");
    await shot(`/movies/${ids["Arrival"]}`, "movie", "Arrival");
    await shot("/activity", "activity", /Grabbed Dune/);
    await shot("/queue", "transfers", /Dune/);
    await shot("/settings/quality", "quality", /quality/i);
  });
});

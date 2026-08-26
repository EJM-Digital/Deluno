/**
 * README screenshots, taken from the live install on the simulation VM so the
 * app has real libraries, a real client, a real indexer and a real import in it.
 */
import { chromium } from "@playwright/test";
import path from "node:path";

const BASE = process.env.DELUNO_URL ?? "http://192.168.1.142:5099";
const CREDS = {
  username: process.env.DELUNO_E2E_USERNAME ?? "admin",
  password: process.env.DELUNO_E2E_PASSWORD ?? "***REMOVED***"
};
const outDir = process.argv[2] ?? "screenshots";

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1600, height: 950 }, deviceScaleFactor: 2 });
const page = await ctx.newPage();

await page.goto(`${BASE}/login`, { waitUntil: "domcontentloaded" });
const resp = await page.request.post(`${BASE}/api/auth/login`, {
  data: CREDS,
  headers: { "Content-Type": "application/json" }
});
if (!resp.ok()) throw new Error(`Login failed: ${resp.status()} ${await resp.text()}`);
const { accessToken, user } = await resp.json();
await page.evaluate(([token, userJson]) => {
  sessionStorage.setItem("deluno-auth-token", token);
  sessionStorage.setItem("deluno-auth-user", userJson);
  // The README set is dark; keep it consistent regardless of what this
  // profile last chose.
  localStorage.setItem("deluno-theme", "dark");
}, [accessToken, JSON.stringify(user)]);

async function shot(url, filename, waitFor) {
  await page.goto(`${BASE}${url}`, { waitUntil: "networkidle" });
  if (waitFor) await page.waitForSelector(waitFor, { timeout: 10_000 }).catch(() => {});
  await page.waitForTimeout(1500);
  await page.screenshot({ path: path.join(outDir, filename), fullPage: false });
  console.log(`  saved ${filename}`);
}

console.log(`Taking screenshots from ${BASE}…`);
await shot("/", "dashboard.png", "text=Dashboard");
await shot("/movies", "movies.png", "text=Movies");
await shot("/tv", "shows.png", "text=TV Shows");
await shot("/queue", "queue.png");
await shot("/settings/profiles", "quality.png", "text=Quality Profiles");
await shot("/indexers/indexers", "indexers.png", "text=Indexers");
await shot("/activity", "activity.png", "text=Activity");

await browser.close();
console.log("Done.");

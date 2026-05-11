/**
 * Takes screenshots of the running app for the README.
 * Run with: node scripts/take-screenshots.mjs
 * Requires the app to be running on localhost:5173 (logged in session not needed — uses bootstrap).
 */
import { chromium } from "@playwright/test";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.resolve(__dirname, "../docs/screenshots");

const BASE = "http://localhost:5173";
const CREDS = { username: "deluno-smoke", password: "deluno-smoke-password" };

async function login(page) {
  // Hit the API directly to get a token, then inject it into sessionStorage
  await page.goto(`${BASE}/login`, { waitUntil: "domcontentloaded" });
  const resp = await page.request.post(`${BASE}/api/auth/login`, {
    data: { username: CREDS.username, password: CREDS.password },
    headers: { "Content-Type": "application/json" }
  });
  if (!resp.ok()) throw new Error(`Login failed: ${resp.status()} ${await resp.text()}`);
  const { accessToken, user } = await resp.json();
  // Inject token into sessionStorage (matching use-auth.tsx storage keys)
  await page.evaluate(([token, userJson]) => {
    sessionStorage.setItem("deluno-auth-token", token);
    sessionStorage.setItem("deluno-auth-user", userJson);
  }, [accessToken, JSON.stringify(user)]);
}

async function shot(page, url, filename, waitFor) {
  await page.goto(`${BASE}${url}`, { waitUntil: "networkidle" });
  if (waitFor) await page.waitForSelector(waitFor, { timeout: 8_000 }).catch(() => {});
  await page.waitForTimeout(600);
  await page.screenshot({ path: path.join(outDir, filename), fullPage: false });
  console.log(`  saved ${filename}`);
}

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1400, height: 800 } });
const page = await ctx.newPage();

// Bootstrap or login
const status = await page.request.get(`${BASE}/api/auth/bootstrap-status`);
const { requiresSetup } = await status.json().catch(() => ({ requiresSetup: false }));
if (requiresSetup) {
  await page.request.post(`${BASE}/api/auth/bootstrap`, {
    data: { displayName: "Deluno User", ...CREDS }
  });
}
await login(page);

console.log("Taking screenshots…");
await shot(page, "/",                    "01-overview.jpg",  "text=Overview");
await shot(page, "/movies",              "02-movies.jpg",    "text=Movies");
await shot(page, "/tv",                  "03-tv.jpg",        "text=TV Shows");
await shot(page, "/indexers",            "04-sources.jpg",   "text=Sources and clients");
await shot(page, "/activity",            "05-activity.jpg",  "text=Activity");
await shot(page, "/settings/quality",    "06-quality.jpg",   "text=Size Rules");
await shot(page, "/calendar",            "07-calendar.jpg",  "text=Calendar");

await browser.close();
console.log("Done.");

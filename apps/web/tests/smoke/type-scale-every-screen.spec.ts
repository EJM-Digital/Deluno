import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { trackDataRequests } from "../helpers/data-settled";
import { waitForLayoutCalm } from "../helpers/layout-calm";
import { serveLiveBusy } from "../helpers/live-busy";
import { settingsRoutes, sidebarRoutes } from "../helpers/route-lists";

// #1447. Every screen of the smoke walk, at a laptop and a wide desktop: each visible text element's computed font size
// must be one of the sizes the type tokens in index.css produce (--type-*, the button, field, list-row and metric tokens,
// all times --font-scale). A size set by hand (12.5px, 13.4px) fails here, so drift cannot come back. The weight, line
// height and figure style of every element is recorded too and printed with the offenders, so a role that looks different
// in two places is seen.

const SIZES = [
  { width: 1920, height: 1080 },
  { width: 1366, height: 768 }
] as const;

// Exceptions: a size that is allowed although it is not a token, each with a plain reason. Keep this list short.
// "selector" is matched against the element and its ancestors; the reason is printed when the audit passes it.
const EXCEPTIONS: { selector: string; reason: string }[] = [
  // Text drawn by the browser or a library in its own size, not by Deluno's styles.
  { selector: "svg text", reason: "Words inside a drawn chart or icon are sized by the drawing, not by the page." },
  { selector: ".lsh-poster[data-size=\"small\"] .lsh-tag", reason: "On a small poster the tag collapses to a coloured bar and its words are hidden on purpose (font-size 0)." }
];

const TOKENS = [
  "--type-micro", "--type-caption", "--type-control", "--type-body-sm", "--list-row-text", "--type-body", "--type-body-lg",
  "--type-card-title", "--type-figure", "--type-title-sm", "--type-title-md", "--type-title-lg", "--type-title-xl", "--type-display",
  "--type-hero-sm", "--type-hero-lg", "--button-font-size", "--button-font-size-sm", "--field-label-size", "--field-help-size",
  "--field-control-size", "--metric-value-size", "--metric-unit-size", "--metric-meta-size", "--metric-label-size"
];

type Seen = { size: number; weight: string; lineHeight: string; figures: string; sample: string; path: string };

function audit({ tokens, exceptions }: { tokens: string[]; exceptions: string[] }) {
  const allowed = new Set<number>();
  const probe = document.createElement("span");
  probe.style.cssText = "position:absolute;visibility:hidden";
  document.body.appendChild(probe);
  for (const token of tokens) {
    probe.style.fontSize = `var(${token})`;
    allowed.add(Math.round(parseFloat(getComputedStyle(probe).fontSize) * 100) / 100);
  }
  probe.remove();
  const seen: Seen[] = [];
  const bad: Seen[] = [];
  for (const el of Array.from(document.querySelectorAll<HTMLElement>("body *"))) {
    if (["SCRIPT", "STYLE", "NOSCRIPT"].includes(el.tagName)) continue;
    const own = Array.from(el.childNodes).filter((node) => node.nodeType === Node.TEXT_NODE).map((node) => node.textContent ?? "").join("").trim();
    if (!own) continue;
    const box = el.getBoundingClientRect();
    const style = getComputedStyle(el);
    if (box.width < 1 || box.height < 1 || style.visibility === "hidden" || style.display === "none") continue;
    const size = Math.round(parseFloat(style.fontSize) * 100) / 100;
    const path: string[] = [];
    for (let node: HTMLElement | null = el; node && path.length < 4 && node !== document.body; node = node.parentElement) {
      path.push(`${node.tagName.toLowerCase()}${typeof node.className === "string" && node.className ? "." + node.className.trim().split(/\s+/).slice(0, 2).join(".") : ""}`);
    }
    const row = { size, weight: style.fontWeight, lineHeight: style.lineHeight, figures: style.fontVariantNumeric, sample: own.slice(0, 40), path: path.join(" < ") };
    seen.push(row);
    if (!allowed.has(size) && !exceptions.some((selector) => el.closest(selector))) bad.push(row);
  }
  return { bad, seen, allowed: Array.from(allowed).sort((a, b) => a - b) };
}

// A film page and a show page as the rig has them (the fit spec's fixtures): a logo, alternate titles, every fact, a long synopsis and cast.
const json = (body: unknown) => ({ contentType: "application/json", body: JSON.stringify(body) });
const FIT_LOGO = `data:image/svg+xml;utf8,${encodeURIComponent("<svg xmlns='http://www.w3.org/2000/svg' width='288' height='48'><rect width='288' height='48' fill='#888'/></svg>")}`;
const CAST = [
  { name: "Christopher Montgomery Featherstonehaugh-Cholmondeley", character: "Lieutenant Commander Bartholomew Winterbottom-Smythe" },
  { name: "Arabella Guinevere Pennington-Whitcombe", character: "Dowager Countess of Little Wittering-on-the-Marsh" }
].map((person, index) => ({ ...person, personId: `type-person-${index}`, profileUrl: null, imdbUrl: null }));
const META = { cast: CAST, logoUrl: FIT_LOGO, alternateTitles: ["La Dimension Desconocida", "Das Unheimliche Zwielicht"], Network: "A Very Long Network Name", Studio: "Cayuga Productions And Partners", OriginalLanguage: "English", Collection: "A Collection With A Rather Long Name", Director: "Rod Serling And Others", Status: "Ended", Certification: "TV-14" };
const SYNOPSIS = "A documentary crew follows a small town through one long winter, and the longer they stay the less sure they are of what they came to film.";

async function seedTitles(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  const headers = token ? { Authorization: `Bearer ${token}` } : {};
  const stamp = Date.now();
  const movie = await (await page.request.post("/api/movies/", { headers, data: { title: `Type check movie ${stamp}`, releaseYear: 1962, monitored: true } })).json() as { id: string };
  const series = await (await page.request.post("/api/series/", { headers, data: { title: `Type check show ${stamp}`, startYear: 2024, monitored: true } })).json() as { id: string };
  for (const [kind, id] of [["series", series.id], ["movies", movie.id]] as const) {
    await page.route(new RegExp(`/api/${kind}/${id}$`), async (route) => {
      if (route.request().method() !== "GET") return route.continue();
      const response = await route.fetch();
      const body = await response.json() as Record<string, unknown>;
      return route.fulfill({ response, json: { ...body, overview: SYNOPSIS, genres: "Drama, Fantasy, Mystery", metadataJson: JSON.stringify(META) } });
    });
  }
  return {
    routes: [`/movies/${movie.id}`, `/tv/${series.id}`],
    async remove() {
      await page.request.delete(`/api/movies/${movie.id}`, { headers });
      await page.request.delete(`/api/series/${series.id}`, { headers });
    }
  };
}

async function walk(page: Page, data: ReturnType<typeof trackDataRequests>, route: string, size: { width: number; height: number }, offenders: string[], roles: string[]) {
  await page.goto(route);
  await data.settled();
  await waitForLayoutCalm(page);
  const result = await page.evaluate(audit, { tokens: TOKENS, exceptions: EXCEPTIONS.map((item) => item.selector) });
  const grouped = new Map<string, Seen[]>();
  for (const item of result.bad) {
    const key = `${item.size}px`;
    grouped.set(key, [...(grouped.get(key) ?? []), item]);
  }
  for (const [key, items] of grouped) {
    offenders.push(`${route} @${size.width}: ${key} x${items.length} e.g. ${items.slice(0, 3).map((item) => `"${item.sample}" [${item.path}]`).join(" | ")}`);
  }
  if (route === "/") for (const item of result.seen) roles.push(`${item.size}px w${item.weight} lh${item.lineHeight} ${item.figures} "${item.sample}" [${item.path}]`);
}

test.describe("type scale on every screen", () => {
  test.skip(({ isMobile }) => isMobile, "Sizes are set in the test itself.");

  for (const size of SIZES) {
    test(`every visible text size is on the type scale at ${size.width}x${size.height}`, async ({ page }) => {
      // The walk adds the routed screens the sidebar and settings lists do not name (search, the system docs and updates pages) and a film and a show.
      const routes = [...new Set([...sidebarRoutes(), ...settingsRoutes(), "/?tab=system", "/search", "/system/docs", "/system/updates"])];
      test.setTimeout(60_000 + routes.length * 8_000);
      await page.setViewportSize(size);
      await authenticateAndNavigate(page, "/");
      await serveLiveBusy(page);
      // The setup guide saves a draft as it opens; the walk must not leave that request hanging (the other walk answers it too).
      await page.route("**/api/setup/draft", (route) => route.request().method() === "PUT" ? route.fulfill({ contentType: "application/json", body: "{}" }) : route.continue());
      const data = trackDataRequests(page);
      const titles = await seedTitles(page);
      const only = process.env.TYPE_ONLY;
      const offenders: string[] = [];
      const roles: string[] = [];
      for (const route of [...routes, ...titles.routes].filter((item) => !only || only.split(",").some((part) => (part === "home" ? item === "/" : item.includes(part))))) {
        await walk(page, data, route, size, offenders, roles);
      }
      await titles.remove();
      if (process.env.TYPE_ROLES) console.log(`ROLES on / at ${size.width}:\n${roles.join("\n")}`);
      if (offenders.length > 0) console.log(`TYPE OFFENDERS at ${size.width}x${size.height}:\n${offenders.join("\n")}`);
      expect(offenders, offenders.join("\n")).toEqual([]);
    });
  }
});

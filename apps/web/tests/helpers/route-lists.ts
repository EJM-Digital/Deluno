import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

// Route lists the fit specs share, read from the router and the shell rather than copied, so a new screen is
// covered the day it is added.

const here = path.dirname(fileURLToPath(import.meta.url));
const src = path.resolve(here, "../../src");

/** Every settings destination the router and the tab strips know about. */
export function settingsRoutes(): string[] {
  const router = readFileSync(path.join(src, "router.tsx"), "utf8");
  const shell = readFileSync(path.join(src, "components/app/settings-shell.tsx"), "utf8");
  const routes = new Set<string>(["/settings"]);
  // Nested children of the "settings" route, and flat "settings/..." routes.
  const block = router.slice(router.indexOf('path: "settings",'), router.indexOf('path: "system",'));
  for (const match of block.matchAll(/path: "([a-z][a-z0-9-]*)",\s*\n\s*lazy/g)) routes.add(`/settings/${match[1]}`);
  for (const match of router.matchAll(/path: "(settings\/[a-z0-9-]+)"/g)) routes.add(`/${match[1]}`);
  // Every tab of every area strip (settings, indexers, search-cycles, system).
  for (const match of shell.matchAll(/to: "(\/(?:settings|indexers|search-cycles|system)[a-z0-9/-]*)"/g)) routes.add(match[1]);
  return [...routes].sort();
}

/** The sidebar's own destinations: the library group and Transfers/Activity. */
export function sidebarRoutes(): string[] {
  const layout = readFileSync(path.join(src, "layouts/app-layout.tsx"), "utf8");
  const routes = new Set<string>();
  for (const name of ["libraryNav", "operationsNav"]) {
    const start = layout.indexOf(`const ${name} = [`);
    const block = layout.slice(start, layout.indexOf("] as const;", start));
    for (const match of block.matchAll(/to: "(\/[a-z0-9/-]*)"/g)) routes.add(match[1]);
  }
  return [...routes];
}

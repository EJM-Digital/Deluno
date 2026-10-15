import { execFileSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../..");

interface InventoryItem { kind: string; key: string; redirect?: boolean }

/**
 * Every screen route of the app, read from the same inventory the coverage register is checked against
 * (scripts/coverage/inventory.mjs reads apps/web/src/router.tsx), so a new screen is walked the day it is added.
 * Redirects are left out (they have no screen of their own) and so are routes with a parameter, which are opened by
 * the specs that seed the title they need.
 */
export function screenRoutes(): string[] {
  const out = execFileSync(process.execPath, [path.join(repoRoot, "scripts/coverage/inventory.mjs"), "--json", "--no-scenarios"], { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  const items = JSON.parse(out) as InventoryItem[];
  return items
    .filter((item) => item.kind === "webroute" && !item.redirect)
    .map((item) => item.key.slice("web:".length))
    .filter((route) => !route.includes(":") && route !== "/login" && route !== "/setup");
}

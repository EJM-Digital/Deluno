// Fails when the brand logo (apps/web/public/favicon.svg) changed but installer/deluno.ico was not rebuilt from it
// (#1129: the installer showed the old logo for five months after the brand changed). Fix by running
// `npm run build:icon`, which rewrites the .ico and its stamp together.
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const svg = readFileSync(join(root, "apps/web/public/favicon.svg"), "utf8").replace(/\r\n/g, "\n");
const stampPath = join(root, "installer/deluno.ico.source-sha256");
const want = createHash("sha256").update(svg).digest("hex");
const have = existsSync(stampPath) ? readFileSync(stampPath, "utf8").trim().split(/\s+/)[0] : "(no stamp)";

if (have !== want) {
  console.error(
    `installer/deluno.ico was not built from the current logo: favicon.svg is ${want.slice(0, 12)}, the icon was built from ${have.slice(0, 12)}. Run npm run build:icon.`
  );
  process.exit(1);
}
console.log("installer/deluno.ico is built from the current favicon.svg.");

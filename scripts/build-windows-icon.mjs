// Builds installer/deluno.ico from the brand logo, apps/web/public/favicon.svg (#1129).
//
// The .ico is what Windows shows for the installer, the tray app, the taskbar, Alt-Tab and the Start menu. It was
// drawn by hand in May and never redrawn when the brand changed in August, so the installer kept the old logo for
// five months. Now it is only ever made from the logo the web app shows: run `npm run build:icon` after changing
// favicon.svg. `scripts/check-windows-icon.mjs` (run by ci:check) fails when the logo changes and this was not run.
//
// The logo's "D" is SVG text in Inter, so the page that renders it loads the same Inter the web app ships; without it
// Chromium would quietly fall back to Arial. Each size is rendered on its own (not scaled down from 256) so the small
// ones stay sharp, and every image is stored as PNG, which Windows Vista and later read inside an .ico.
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const svgPath = join(root, "apps/web/public/favicon.svg");
const icoPath = join(root, "installer/deluno.ico");
const stampPath = join(root, "installer/deluno.ico.source-sha256");
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

const require = createRequire(join(root, "package.json"));
const { chromium } = require("playwright");
const interFont = require.resolve("@fontsource-variable/inter/files/inter-latin-wght-normal.woff2", {
  paths: [join(root, "apps/web")]
});

const svg = readFileSync(svgPath, "utf8");
// Inlined: a page made with setContent may not load file:// URLs.
const interData = `data:font/woff2;base64,${readFileSync(interFont).toString("base64")}`;
const browser = await chromium.launch();
const images = [];
try {
  for (const size of sizes) {
    const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
    await page.setContent(`<!doctype html><html><head><style>
      @font-face { font-family: "Inter"; src: url("${interData}") format("woff2"); font-weight: 100 900; }
      html, body { margin: 0; padding: 0; background: transparent; }
      svg { display: block; width: ${size}px; height: ${size}px; }
    </style></head><body>${svg}</body></html>`);
    await page.evaluate(() => document.fonts.load('900 372px "Inter"'));
    await page.evaluate(() => document.fonts.ready);
    if (!(await page.evaluate(() => document.fonts.check('900 372px "Inter"')))) {
      throw new Error("Inter did not load, so the logo's D would be drawn in a fallback font.");
    }
    images.push({ size, png: await page.screenshot({ omitBackground: true, type: "png" }) });
    await page.close();
  }
} finally {
  await browser.close();
}

// ICONDIR, then one ICONDIRENTRY per image, then the PNGs. A width or height of 256 is written as 0.
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(images.length, 4);
let offset = 6 + 16 * images.length;
const entries = images.map(({ size, png }) => {
  const entry = Buffer.alloc(16);
  entry.writeUInt8(size >= 256 ? 0 : size, 0);
  entry.writeUInt8(size >= 256 ? 0 : size, 1);
  entry.writeUInt8(0, 2);
  entry.writeUInt8(0, 3);
  entry.writeUInt16LE(1, 4);
  entry.writeUInt16LE(32, 6);
  entry.writeUInt32LE(png.length, 8);
  entry.writeUInt32LE(offset, 12);
  offset += png.length;
  return entry;
});
writeFileSync(icoPath, Buffer.concat([header, ...entries, ...images.map((image) => image.png)]));

const svgHash = createHash("sha256").update(svg.replace(/\r\n/g, "\n")).digest("hex");
writeFileSync(stampPath, `${svgHash}  apps/web/public/favicon.svg\n`);
console.log(`Wrote installer/deluno.ico (${sizes.join(", ")} px) from favicon.svg ${svgHash.slice(0, 12)}.`);

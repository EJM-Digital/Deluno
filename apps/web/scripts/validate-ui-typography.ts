#!/usr/bin/env node

/**
 * Guards the density-aware typography and semantic colour system.
 *
 * A small number of literal colours are data: tag swatches, custom-format
 * category metadata, and the two provider brand treatments. Keep those
 * exceptions explicit so a new one is visible in review.
 */
import { readFileSync, readdirSync, statSync } from "fs";
import { dirname, join, relative } from "path";
import { fileURLToPath } from "url";

const here = dirname(fileURLToPath(import.meta.url));
const sourceRoot = join(here, "..", "src");
const sourceExtensions = new Set([".ts", ".tsx"]);

// A literal pixel font size, e.g. text-[11px] or text-[8.5px]. This does not
// match density-aware values such as text-[length:var(--type-caption)].
const arbitraryTextSize = /(?<![\w-])text-\[[0-9.]+px\]/g;

// A raw Tailwind palette colour on any colour-bearing utility.
const paletteColour = /(?<![\w-])(?:bg|text|border|ring|from|via|to|decoration|outline|shadow)-(?:slate|gray|zinc|neutral|stone|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose)-(?:50|[1-9]00|950)(?![\w-])/g;

const ALLOWED_PALETTE_FILES = new Map([
  ["routes/settings-tags-page.tsx", "tag swatch colours are user data, not theme styling"],
  ["lib/trash-guide-data.ts", "custom-format category colours are catalogue data"],
  ["components/app/rating-strip.tsx", "TMDB and IMDb brand colours"]
]);

const failures: string[] = [];

function visit(directory: string) {
  for (const entry of readdirSync(directory)) {
    const filePath = join(directory, entry);
    const stat = statSync(filePath);
    if (stat.isDirectory()) {
      visit(filePath);
      continue;
    }
    if (!sourceExtensions.has(filePath.slice(filePath.lastIndexOf(".")))) continue;

    const relativePath = relative(sourceRoot, filePath).replaceAll("\\", "/");
    readFileSync(filePath, "utf8").split(/\r?\n/).forEach((line, index) => {
      arbitraryTextSize.lastIndex = 0;
      const sizes = line.match(arbitraryTextSize);
      if (sizes) {
        failures.push(`${relativePath}:${index + 1} uses ${sizes.join(", ")}; use text-[length:var(--type-*)] or a .type-* utility`);
      }

      if (ALLOWED_PALETTE_FILES.has(relativePath)) return;
      paletteColour.lastIndex = 0;
      const colours = line.match(paletteColour);
      if (colours) {
        failures.push(`${relativePath}:${index + 1} uses ${colours.join(", ")}; use semantic colour tokens such as text-warning or bg-destructive`);
      }
    });
  }
}

visit(sourceRoot);

if (failures.length) {
  console.error("UI typography check failed. Use density-aware type tokens and semantic colour tokens:");
  failures.forEach((failure) => console.error(`  ${failure}`));
  process.exit(1);
}

console.log("UI typography check passed: all font sizes and theme colours use shared tokens or documented data allowlists.");

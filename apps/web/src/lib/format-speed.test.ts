import { describe, expect, it } from "vitest";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { formatBytesPerSecond, formatSpeed, speedAxisLabel, speedParts, speedUnitFor } from "./format-speed";

describe("formatSpeed", () => {
  it.each([
    [0, "0 KB/s"],
    [0.001, "1 KB/s"],
    [0.12, "120 KB/s"],
    [0.999, "999 KB/s"],
    [0.9996, "1.0 MB/s"],
    [1, "1.0 MB/s"],
    [3.44, "3.4 MB/s"],
    [9.95, "10 MB/s"],
    [10, "10 MB/s"],
    [42.4, "42 MB/s"],
    [999, "999 MB/s"],
    [999.6, "1.0 GB/s"],
    [1000, "1.0 GB/s"],
    [1100, "1.1 GB/s"]
  ])("says %f MB/s as %s", (mbps, text) => {
    expect(formatSpeed(mbps)).toBe(text);
  });

  it("keeps each screen's own words for 0 and treats junk as 0", () => {
    expect(formatSpeed(0, { zero: "Not moving" })).toBe("Not moving");
    expect(formatSpeed(NaN)).toBe("0 KB/s");
    expect(formatSpeed(null)).toBe("0 KB/s");
    expect(formatSpeed(-5)).toBe("0 KB/s");
    expect(formatSpeed(2, { zero: "Not moving" })).toBe("2.0 MB/s");
  });

  it("gives the number and unit apart", () => {
    expect(speedParts(0.12)).toEqual({ value: "120", unit: "KB/s" });
    expect(speedUnitFor(1)).toBe("MB/s");
  });

  it("reads bytes per second on the same base", () => {
    expect(formatBytesPerSecond(120_000)).toBe("120 KB/s");
    expect(formatBytesPerSecond(5_000_000)).toBe("5.0 MB/s");
  });

  it("labels an axis in the unit of its largest value", () => {
    expect(speedAxisLabel(0.025, "KB/s")).toBe("25 KB/s");
    expect(speedAxisLabel(2.5, "MB/s")).toBe("2.5 MB/s");
    expect(speedAxisLabel(12, "MB/s")).toBe("12 MB/s");
    expect(speedAxisLabel(1500, "GB/s")).toBe("1.5 GB/s");
  });
});

describe("no other file builds a speed string", () => {
  const root = join(__dirname, "..");
  const files: string[] = [];
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const path = join(dir, name);
      if (statSync(path).isDirectory()) walk(path);
      else if (/\.(ts|tsx)$/.test(name) && !/\.test\.(ts|tsx)$/.test(name)) files.push(path);
    }
  };
  walk(root);

  // A person typing a limit chooses its unit; that is not a reading.
  const chosenUnit = new Set(["routes/settings-download-health-page.tsx", "routes/setup-guide-page.tsx"]);

  it("has no hard-coded MB/s, KB/s or GB/s in a built string", () => {
    const offenders = files
      .map((file) => relative(root, file).split(sep).join("/"))
      .filter((file) => file !== "lib/format-speed.ts" && !chosenUnit.has(file))
      .filter((file) => /[A-Z]B\/s/.test(readFileSync(join(root, file), "utf8").split("\n").filter((line) => !/^\s*(\/\/|\*|\/\*)/.test(line)).join("\n")));
    expect(offenders).toEqual([]);
  });
});

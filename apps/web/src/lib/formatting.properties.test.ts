import fc from "fast-check";
import { describe, expect, it } from "vitest";
import { formatEta, formatSpeed, until, ago } from "../routes/dashboard/downloads-kit";
import { formatBytes, formatBytesFromGb } from "./utils";

const formatSize = formatBytes;
const DASH = "—";
import { formatRuntime } from "./display-preferences";

/** #1271: size, speed and time formatting, as properties over any number rather than a few examples. */

const UNITS = ["B", "KB", "MB", "GB", "TB"];
// Every number a byte count can be: not a fraction of a byte (a defect is pinned below), and below 1e15 (a petabyte).
const anyNumber = fc.oneof(
  fc.double({ min: -1e15, max: 1e15, noNaN: true }).filter((n) => !(n > 0 && n < 1)),
  fc.integer({ min: -5, max: 5_000_000 }),
  fc.constantFrom(NaN, Infinity, -Infinity, 0, -0, 1023.5, 1048575, Number.MAX_SAFE_INTEGER),
);
const nonNegativeBytes = fc.double({ min: 0, max: 1e15, noNaN: true });

/** The byte count a "12.5 GB" reading stands for. */
function bytesOf(text: string): number {
  const [amount, unit] = text.split(" ");
  return Number(amount) * 1024 ** UNITS.indexOf(unit);
}

describe("formatSize properties", () => {
  it("never reads NaN, undefined or Infinity for any input at all", () => {
    fc.assert(
      fc.property(anyNumber, (n) => {
        const text = formatSize(n);
        if (Number.isFinite(n) && n >= 0) expect(text).toMatch(/^\d+(\.\d)? (B|KB|MB|GB|TB)$/);
        else expect(text).toBe(DASH);
      }),
    );
  });

  it("uses a unit from B to TB for every whole-byte count of 1 or more", () => {
    fc.assert(
      fc.property(fc.integer({ min: 1, max: Number.MAX_SAFE_INTEGER }), (n) => {
        expect(UNITS).toContain(formatSize(n).split(" ")[1]);
      }),
    );
  });

  it("reads zero as 0 B and null, undefined, negatives and non-finite sizes as a plain dash", () => {
    expect(formatSize(0)).toBe("0 B");
    expect(formatSize(null)).toBe(DASH);
    expect(formatSize(undefined)).toBe(DASH);
    expect(formatSize(NaN)).toBe(DASH);
    expect(formatSize(Infinity)).toBe(DASH);
    fc.assert(fc.property(fc.double({ max: -Number.MIN_VALUE, noNaN: true }), (n) => { expect(formatSize(n)).toBe(DASH); }));
  });

  it("is monotonic: a larger size never reads as a smaller amount (whole bytes)", () => {
    fc.assert(
      fc.property(fc.integer({ min: 1, max: 2 ** 50 }), fc.integer({ min: 0, max: 2 ** 40 }), (a, extra) => {
        expect(bytesOf(formatSize(a + extra))).toBeGreaterThanOrEqual(bytesOf(formatSize(a)) - 1e-9);
      }),
    );
  });

  it("is within rounding of the real size, so the unit is correct", () => {
    fc.assert(
      fc.property(fc.integer({ min: 1, max: 2 ** 50 }), (n) => {
        const shown = bytesOf(formatSize(n));
        expect(Math.abs(shown - n) / n).toBeLessThan(0.06);
      }),
    );
  });

  it("never shows a value of 1024 or more in a unit that has a larger one (just under a unit boundary) - DEFECT: 1048575 reads 1024 KB", () => {
    fc.assert(
      fc.property(fc.integer({ min: 1, max: 4 }), fc.integer({ min: 1, max: 600 }), (power, below) => {
        const [amount, unit] = formatSize(1024 ** power - below).split(" ");
        if (unit !== "TB") expect(Number(amount)).toBeLessThan(1024);
      }),
    );
  });

  it("DEFECT (shrunk case): a size just under 1 MB reads 1024 KB, not 1 MB", () => {
    expect(formatSize(1048575)).toBe("1.0 MB");
  });

  it("DEFECT (shrunk case): half a byte reads with a missing unit", () => {
    expect(formatSize(0.5)).toBe("1 B");
  });
});

describe("formatBytes properties", () => {
  it("a subnormal size reads 0 B, not Infinity", () => {
    expect(formatBytes(Number.MIN_VALUE)).toBe("0 B");
  });

  it("a negative size reads a dash", () => {
    expect(formatBytes(-5)).toBe(DASH);
  });

  it("DEFECT (shrunk case): half a byte reads with a missing unit", () => {
    expect(formatBytes(0.5)).toBe("1 B");
  });

  it("never reads NaN or undefined for a count of bytes of 1 or more", () => {
    fc.assert(
      fc.property(fc.integer({ min: 1, max: Number.MAX_SAFE_INTEGER }), (n) => {
        expect(formatBytes(n)).toMatch(/^\d+(\.\d)? (B|KB|MB|GB|TB)$/);
      }),
    );
  });

  it("never reads NaN or undefined for any size of 1 byte to a petabyte", () => {
    fc.assert(
      fc.property(fc.double({ min: 1, max: 1e15, noNaN: true }), (n) => {
        expect(formatBytes(n)).not.toMatch(/NaN|undefined/);
      }),
    );
  });

  it("is within rounding of the real size", () => {
    fc.assert(
      fc.property(fc.integer({ min: 1, max: 2 ** 50 }), (n) => {
        const shown = bytesOf(formatBytes(n));
        expect(Math.abs(shown - n) / n).toBeLessThan(0.06);
      }),
    );
  });
});

describe("formatBytesFromGb properties", () => {
  it("always ends in GB or says Unknown, and never reads NaN", () => {
    fc.assert(
      fc.property(fc.oneof(fc.double({ min: -1e15, max: 1e15 }), fc.constant(NaN), fc.constant(Infinity), fc.constant(null), fc.constant(undefined)), (n) => {
        const text = formatBytesFromGb(n);
        expect(text === "Unknown" || /^-?\d+\.\d GB$/.test(text)).toBe(true);
      }),
    );
  });
});

describe("formatSpeed properties", () => {
  it("always reads a number followed by MB/s, never NaN or negative", () => {
    fc.assert(
      fc.property(fc.oneof(fc.double({ min: -1e15, max: 1e15 }), fc.constant(NaN), fc.constant(Infinity), fc.constantFrom(null, undefined)), (n) => {
        expect(formatSpeed(n)).toMatch(/^\d+(\.\d)? MB\/s$/);
      }),
    );
  });

  it("is monotonic: a faster speed never reads as slower", () => {
    fc.assert(
      fc.property(fc.double({ min: 0, max: 5000, noNaN: true }), fc.double({ min: 0, max: 5000, noNaN: true }), (a, b) => {
        const [lo, hi] = a <= b ? [a, b] : [b, a];
        expect(parseFloat(formatSpeed(hi))).toBeGreaterThanOrEqual(parseFloat(formatSpeed(lo)));
      }),
    );
  });

  it("is within half a unit of the real speed", () => {
    fc.assert(
      fc.property(fc.double({ min: 0, max: 5000, noNaN: true }), (n) => {
        expect(Math.abs(parseFloat(formatSpeed(n)) - n)).toBeLessThanOrEqual(0.5 + 1e-9);
      }),
    );
  });
});

describe("formatEta properties", () => {
  it("reads a dash or a plain duration, never NaN, for any input", () => {
    fc.assert(
      fc.property(fc.oneof(fc.double({ min: -1e12, max: 1e12 }), fc.constant(NaN), fc.integer({ min: -10, max: 10_000_000 }), fc.constantFrom(null, undefined)), (n) => {
        expect(formatEta(n)).toMatch(/^(–|under 1 min|\d+ min|\d+ h( \d+ min)?|\d+ days)$/);
      }),
    );
  });

  it("is monotonic in minutes: more time left never reads as fewer minutes", () => {
    const minutesOf = (text: string): number => {
      if (text === "–") return 0;
      if (text === "under 1 min") return 0;
      const days = /^(\d+) days$/.exec(text);
      if (days) return Number(days[1]) * 1440;
      const hm = /^(?:(\d+) h)?(?: ?(\d+) min)?$/.exec(text)!;
      return Number(hm[1] ?? 0) * 60 + Number(hm[2] ?? 0);
    };
    fc.assert(
      fc.property(fc.integer({ min: 1, max: 5_000_000 }), fc.integer({ min: 0, max: 100_000 }), (a, extra) => {
        expect(minutesOf(formatEta(a + extra))).toBeGreaterThanOrEqual(minutesOf(formatEta(a)));
      }),
    );
  });
});

describe("formatRuntime properties", () => {
  it("never reads NaN for a runtime and round trips hours and minutes", () => {
    fc.assert(
      fc.property(fc.integer({ min: 0, max: 100_000 }), (m) => {
        const text = formatRuntime(m);
        expect(text).not.toMatch(/NaN/);
        const hm = /^(?:(\d+)h )?(\d+)m$/.exec(text)!;
        expect(Number(hm[1] ?? 0) * 60 + Number(hm[2])).toBe(m);
      }),
    );
  });
});

describe("ago and until properties", () => {
  const now = Date.parse("2026-10-14T12:00:00Z");
  it("ago never reads NaN for any instant and is empty for text that is not a time", () => {
    fc.assert(
      fc.property(fc.integer({ min: -1e10, max: 1e10 }), (offsetSeconds) => {
        const text = ago(new Date(now - offsetSeconds * 1000).toISOString(), now);
        expect(text).not.toMatch(/NaN/);
      }),
    );
    fc.assert(fc.property(fc.string().filter((s) => Number.isNaN(Date.parse(s))), (s) => { expect(ago(s, now)).toBe(""); }));
  });

  it("until never reads NaN and a time already past is due now", () => {
    fc.assert(
      fc.property(fc.integer({ min: -1e8, max: 1e8 }), (offsetSeconds) => {
        const text = until(new Date(now + offsetSeconds * 1000).toISOString(), now);
        expect(text).not.toMatch(/NaN/);
        if (offsetSeconds <= 0) expect(text).toBe("due now");
      }),
    );
  });
});

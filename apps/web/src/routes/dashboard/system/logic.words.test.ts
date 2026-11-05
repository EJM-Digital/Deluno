import { describe, expect, it } from "vitest";
import {
  AREAS, areaName, areaOf, areaOfAttention, clockText, countdown, fullIn, plainCount, sinceText, sizeText, span, splitAddress, timeOfDayText, traceMax, tracePaths, upText, wholeRows, whenText,
  type HealthArea
} from "./logic";

const local = (day: number, h: number, m = 0) => new Date(2026, 10, day, h, m).getTime();
const NOW = local(10, 12, 0);

describe("the health areas", () => {
  it("are these seven, in order, each with its name", () => {
    expect(AREAS.map((a) => [a.key, a.name])).toEqual([["library", "Library"], ["downloads", "Downloads"], ["connections", "Connections"], ["processing", "Processing"], ["storage", "Storage"], ["security", "Security"], ["machine", "Machine"]]);
    for (const area of AREAS) expect(areaName(area.key)).toBe(area.name);
  });
});

describe("the area of a stored check", () => {
  const check = (category: string, id = "x", name = "x") => ({ id, category, name });
  it.each([
    ["library", "library"], ["tags", "library"], ["notice", "library"], ["download-client", "downloads"], ["grab", "downloads"],
    ["indexer", "connections"], ["media-server", "connections"], ["metadata", "connections"], ["subtitle-provider", "connections"], ["seerr", "connections"], ["proxy", "connections"],
    ["processor", "processing"], ["remote-path", "processing"], ["machine", "machine"]
  ])("%s belongs to %s", (category, area) => {
    expect(areaOf(check(category))).toBe(area);
  });

  it("puts a machine check in Machine whatever it says", () => {
    expect(areaOf(check("machine", "machine:drive", "Drive health: password"))).toBe("machine");
  });

  it.each(["Password is weak", "Sign-in is open", "signin", "Allowed host missing", "Security", "LAN access", "Certificate expires", "HTTPS is off", "API key shown", "Authentication"])("a catch-all check saying %s is Security", (name) => {
    expect(areaOf(check("system", "id", name))).toBe("security");
  });

  it.each(["Drive is failing", "Disk is full", "Low free space", "Storage low", "No space", "The volume is full."])("a catch-all check saying %s is Storage", (name) => {
    expect(areaOf(check("system", "id", name))).toBe("storage");
  });

  it.each(["Computer sleep", "Hibernation", "Reboot pending", "SMART warning on drive", "Temperature on disk", "Windows update on drive", "Restart needed for disk", "Service on drive", "Background drive job", "Power plan drive"])("a catch-all check saying %s is Machine", (name) => {
    expect(areaOf(check("system", "id", name))).toBe("machine");
  });

  it("is Machine for a catch-all check about nothing in particular, and reads the id as well as the name", () => {
    expect(areaOf(check("system", "id", "Something"))).toBe("machine");
    expect(areaOf(check("system", "security:thing", "Something"))).toBe("security");
    expect(areaOf(check("other", "disk:one", "Something"))).toBe("storage");
  });
});

describe("the area of a Needs-you row", () => {
  it.each([
    ["stalled-1", "downloads"], ["x-download-y", "downloads"], ["queue-low", "downloads"], ["handoff-failed", "processing"], ["processor-down", "processing"], ["processors", "processing"],
    ["clients", "connections"], ["indexers", "connections"], ["leftover-folders", "library"], ["folder-missing", "library"], ["recovery", "library"],
    ["import-failed", "library"], ["library-empty", "library"], ["storage-low", "storage"], ["disk-full", "storage"], ["no-space", "storage"], ["security-open", "security"], ["password-weak", "security"], ["whatever", "machine"]
  ] as [string, HealthArea][])("%s is in %s", (id, area) => {
    expect(areaOfAttention(id)).toBe(area);
  });

  it("does not take a longer word for the exact connection names", () => {
    expect(areaOfAttention("clients-x")).toBe("machine");
    expect(areaOfAttention("xindexers")).toBe("machine");
  });

  it("looks at where a word starts for stalled and queue", () => {
    expect(areaOfAttention("not-stalled")).toBe("machine");
    expect(areaOfAttention("the-queue")).toBe("machine");
  });
});

describe("the words for readings", () => {
  it("says a size, or nothing for a size it cannot read", () => {
    expect(sizeText(387 * 1024 ** 2)).toBe("387 MB");
    expect(sizeText(null)).toBe("");
    expect(sizeText(undefined)).toBe("");
    expect(sizeText(-1)).toBe("");
  });

  it.each([[null, ""], [undefined, ""], [Number.NaN, ""], [Number.POSITIVE_INFINITY, ""], [0, "up 1 min"], [59, "up 1 min"], [120, "up 2 min"], [59 * 60, "up 59 min"], [3600, "up 1 h"], [47 * 3600, "up 47 h"], [48 * 3600, "up 2 days"], [6.5 * 86400, "up 6 days"]])("an uptime of %s seconds reads %s", (value, text) => {
    expect(upText(value as number | null | undefined)).toBe(text);
  });

  it.each([[-5000, "0:00"], [0, "0:00"], [4000, "0:04"], [64_000, "1:04"], [3_599_000, "59:59"], [3_600_000, "1 h 0 min"], [2 * 3_600_000 + 5 * 60_000, "2 h 5 min"], [86_399_000, "23 h 59 min"], [86_400_000, "1 days"], [3 * 86_400_000, "3 days"]])("%s ms until a task runs reads %s", (ms, text) => {
    expect(countdown(ms)).toBe(text);
  });

  it.each([[0, "1 min"], [89_000, "1 min"], [5 * 60_000, "5 min"], [59 * 60_000, "59 min"], [60 * 60_000, "1 h"], [3 * 3_600_000, "3 h"], [23 * 3_600_000, "23 h"], [24 * 3_600_000, "1 day"], [2 * 86_400_000, "2 days"]])("a span of %s ms reads %s", (ms, text) => {
    expect(span(ms)).toBe(text);
  });

  it("says full in days, or months beyond ninety, and nothing for no answer", () => {
    expect(fullIn(null)).toBeNull();
    expect(fullIn(undefined)).toBeNull();
    expect(fullIn(Number.NaN)).toBeNull();
    expect(fullIn(Number.POSITIVE_INFINITY)).toBeNull();
    expect(fullIn(0)).toBeNull();
    expect(fullIn(-3)).toBeNull();
    expect(fullIn(0.4)).toBe("0 days");
    expect(fullIn(21.4)).toBe("21 days");
    expect(fullIn(90)).toBe("90 days");
    expect(fullIn(90.4)).toBe("90 days");
    expect(fullIn(91)).toBe("3 months");
    expect(fullIn(200)).toBe("7 months");
  });

  it("counts with thousands separated", () => {
    expect(plainCount(1234567)).toBe("1,234,567");
    expect(plainCount(0.6)).toBe("1");
  });
});

describe("addresses", () => {
  it.each([
    ["http://192.168.1.10:5099/", "192.168.1.10", "5099"], ["https://deluno.local/path/x", "deluno.local", null], ["192.168.1.10:80", "192.168.1.10", "80"], ["  host  ", "host", null],
    ["", "", null], [null, "", null], [undefined, "", null], ["host:port", "host:port", null], ["host:80x", "host:80x", null], ["a:b:8080", "a:b", "8080"]
  ])("%s is host %s and port %s", (address, host, port) => {
    expect(splitAddress(address as string | null | undefined)).toEqual({ host, port });
  });
});

describe("clock times", () => {
  it("says a clock time the way Australians read it", () => {
    expect(clockText(new Date(2026, 10, 4, 3, 0))).toBe("3:00 am");
    expect(clockText(new Date(2026, 10, 4, 15, 5))).toBe("3:05 pm");
    expect(clockText(new Date(2026, 10, 4, 0, 30))).toBe("12:30 am");
  });

  it("turns a time-of-day setting into a clock time, or leaves what it cannot read as it is", () => {
    expect(timeOfDayText("03:00")).toBe("3:00 am");
    expect(timeOfDayText("15:45")).toBe("3:45 pm");
    expect(timeOfDayText("3:00:59")).toBe("3:00 am");
    expect(timeOfDayText("soon")).toBe("soon");
    expect(timeOfDayText("x03:00")).toBe("x03:00");
    expect(timeOfDayText("")).toBe("");
    expect(timeOfDayText(null)).toBe("");
    expect(timeOfDayText(undefined)).toBe("");
  });

  it("says when something started: the time today, the day otherwise, nothing for nothing", () => {
    expect(sinceText(new Date(2026, 10, 10, 9, 14).toISOString(), NOW)).toBe("9:14 am");
    expect(sinceText(new Date(2026, 10, 3, 9, 14).toISOString(), NOW)).toBe("3 Nov");
    expect(sinceText(new Date(2025, 10, 10, 9, 14).toISOString(), NOW)).toBe("10 Nov");
    expect(sinceText(new Date(2026, 9, 10, 9, 14).toISOString(), NOW)).toBe("10 Oct");
    expect(sinceText(null, NOW)).toBe("");
    expect(sinceText(undefined, NOW)).toBe("");
    expect(sinceText("", NOW)).toBe("");
    expect(sinceText("garbage", NOW)).toBe("");
  });

  it("reads the clock itself when it is not given one", () => {
    expect(sinceText(new Date().toISOString())).toMatch(/^\d{1,2}:\d{2} [ap]m$/);
  });

  it("says when something happened: today, yesterday, a weekday this week, or the date", () => {
    expect(whenText(new Date(2026, 10, 10, 3, 0).toISOString(), NOW)).toBe("Today 3:00 am");
    expect(whenText(new Date(2026, 10, 11, 3, 0).toISOString(), NOW)).toBe("Today 3:00 am");
    expect(whenText(new Date(2026, 10, 9, 23, 59).toISOString(), NOW)).toBe("Yesterday 11:59 pm");
    expect(whenText(new Date(2026, 10, 8, 3, 0).toISOString(), NOW)).toMatch(/^[A-Z][a-z]{2} 3:00 am$/);
    expect(whenText(new Date(2026, 10, 4, 3, 0).toISOString(), NOW)).toMatch(/^[A-Z][a-z]{2} 3:00 am$/);
    expect(whenText(new Date(2026, 10, 3, 3, 0).toISOString(), NOW)).toBe("3 Nov 3:00 am");
    expect(whenText("garbage", NOW)).toBe("");
  });

  it("reads the clock itself when it is not given one", () => {
    expect(whenText(new Date().toISOString())).toMatch(/^Today /);
  });
});

describe("the traces", () => {
  it("draws a line and the area under it in a 200 by 50 box", () => {
    expect(tracePaths([0, 5, 10], 10)).toEqual({ line: "M0.0,50.0 L100.0,27.0 L200.0,4.0", area: "M0,50 L0.0,50.0 L100.0,27.0 L200.0,4.0 L200,50 Z" });
  });

  it("keeps a reading inside the box", () => {
    expect(tracePaths([-5, 20], 10)?.line).toBe("M0.0,50.0 L200.0,4.0");
  });

  it("leaves out readings that are not numbers, and draws nothing from fewer than two or without a scale", () => {
    expect(tracePaths([1, Number.NaN, 2], 2)?.line).toBe("M0.0,27.0 L200.0,4.0");
    expect(tracePaths([1], 2)).toBeNull();
    expect(tracePaths([], 2)).toBeNull();
    expect(tracePaths([Number.NaN, 1], 2)).toBeNull();
    expect(tracePaths([1, 2], 0)).toBeNull();
    expect(tracePaths([1, 2], -1)).toBeNull();
    expect(tracePaths([1, 2], Number.NaN)).toBeNull();
  });

  it("scales to a fixed top, or to the highest reading with headroom, and never to zero", () => {
    expect(traceMax([1, 2, 3], 100)).toBe(100);
    expect(traceMax([1, 2, 4])).toBe(5);
    expect(traceMax([1, 2, 4], undefined, 2)).toBe(8);
    expect(traceMax([0, 0])).toBe(1);
    expect(traceMax([])).toBe(1);
    expect(traceMax([Number.NaN, 4])).toBe(5);
    expect(traceMax([-5, -1])).toBe(1);
    expect(traceMax([1, 2], 0)).toBe(2.5);
  });
});

describe("how many whole rows fit", () => {
  it("fits as many rows with their gaps as the room holds, and at least one", () => {
    expect(wholeRows(100, 20, 5)).toBe(4);
    expect(wholeRows(104, 20, 5)).toBe(4);
    expect(wholeRows(105, 20, 5)).toBe(4);
    expect(wholeRows(120, 20, 5)).toBe(5);
    expect(wholeRows(5, 20, 5)).toBe(1);
    expect(wholeRows(0, 20, 5)).toBe(1);
    expect(wholeRows(-50, 20, 5)).toBe(1);
  });
});

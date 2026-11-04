import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { describeCallLimitStatus, formatSeconds, healthChip, indexerFlagSelected, indexerHost, indexerReadiness, protocolLabel, relative, scopeLabel, splitIndexerFlags, toggleIndexerFlag, unusedChip, vipWarningActive } from "./format";

const NOW = Date.parse("2026-11-04T12:00:00Z");
const DAY = 24 * 3_600_000;
const iso = (offsetMs: number) => new Date(NOW + offsetMs).toISOString();

beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(NOW); });
afterEach(() => { vi.useRealTimers(); });

describe("a connection that nothing uses", () => {
  const on = { isEnabled: true, healthStatus: "healthy" };
  it("says Not routed over a healthy, switched-on connection that no library uses", () => {
    expect(unusedChip(on, 0, 2)).toEqual({ tone: "warn", label: "Not routed" });
  });

  it("says nothing when it is used, when there are no libraries, when it is off, or when it is not healthy", () => {
    expect(unusedChip(on, 1, 2)).toBeNull();
    expect(unusedChip(on, 0, 0)).toBeNull();
    expect(unusedChip({ isEnabled: false, healthStatus: "healthy" }, 0, 2)).toBeNull();
    expect(unusedChip({ isEnabled: true, healthStatus: "degraded" }, 0, 2)).toBeNull();
  });
});

describe("when a VIP warning is active", () => {
  it("starts seven days before the date and goes on after it", () => {
    expect(vipWarningActive({ vipExpiresUtc: iso(7 * DAY + 1000) })).toBe(false);
    expect(vipWarningActive({ vipExpiresUtc: iso(7 * DAY) })).toBe(true);
    expect(vipWarningActive({ vipExpiresUtc: iso(DAY) })).toBe(true);
    expect(vipWarningActive({ vipExpiresUtc: iso(-30 * DAY) })).toBe(true);
  });

  it("is off with no date, or once that exact date was acknowledged, and back on for a new date", () => {
    expect(vipWarningActive({})).toBe(false);
    expect(vipWarningActive({ vipExpiresUtc: null })).toBe(false);
    expect(vipWarningActive({ vipExpiresUtc: iso(DAY), vipWarningAcknowledgedExpiryUtc: iso(DAY) })).toBe(false);
    expect(vipWarningActive({ vipExpiresUtc: iso(DAY), vipWarningAcknowledgedExpiryUtc: iso(2 * DAY) })).toBe(true);
  });
});

describe("the health chip of a connection", () => {
  const item = (overrides: Record<string, unknown> = {}) => ({ isEnabled: true, healthStatus: "healthy", ...overrides });

  it("says Off for a connection that is switched off, before anything else", () => {
    expect(healthChip(item({ isEnabled: false, healthStatus: "unhealthy" }), true).label).toBe("Off");
  });

  it("says Paused for a client that reports itself paused", () => {
    expect(healthChip(item(), true).label).toBe("Paused");
  });

  it("says Rate-limited while Deluno is backing off, and not once the time has passed", () => {
    expect(healthChip(item({ rateLimitedUntilUtc: iso(60_000) })).label).toBe("Rate-limited");
    expect(healthChip(item({ rateLimitedUntilUtc: iso(-60_000) })).label).toBe("Healthy");
    expect(healthChip(item({ rateLimitedUntilUtc: iso(0) })).label).toBe("Healthy");
    expect(healthChip(item({ rateLimitedUntilUtc: null })).label).toBe("Healthy");
  });

  it("says the VIP is expiring or expired over a healthy or untested connection", () => {
    expect(healthChip(item({ vipExpiresUtc: iso(2 * DAY) }))).toEqual({ tone: "warn", label: "VIP expiring soon" });
    expect(healthChip(item({ vipExpiresUtc: iso(-DAY) }))).toEqual({ tone: "bad", label: "VIP expired" });
    expect(healthChip(item({ vipExpiresUtc: iso(-1000), healthStatus: "untested" })).label).toBe("VIP expired");
    expect(healthChip(item({ vipExpiresUtc: iso(0) })).label).toBe("VIP expired");
    expect(healthChip(item({ vipExpiresUtc: iso(1000) })).label).toBe("VIP expiring soon");
  });

  it("lets a real failure speak first, and a date far off or acknowledged say nothing", () => {
    expect(healthChip(item({ vipExpiresUtc: iso(-DAY), healthStatus: "degraded" })).label).toBe("Degraded");
    expect(healthChip(item({ vipExpiresUtc: iso(30 * DAY) })).label).toBe("Healthy");
    expect(healthChip(item({ vipExpiresUtc: iso(-DAY), vipWarningAcknowledgedExpiryUtc: iso(-DAY) })).label).toBe("Healthy");
  });

  it.each([["healthy", "Healthy"], ["degraded", "Degraded"], ["untested", "Untested"], ["circuitOpen", "Paused after failures"], ["unhealthy", "Unhealthy"], ["anything else", "Unhealthy"]])("says %s as %s", (status, label) => {
    expect(healthChip(item({ healthStatus: status })).label).toBe(label);
  });
});

describe("how long ago", () => {
  it.each([
    [null, "Never"], [undefined, "Never"], ["", "Never"],
    [iso(-20_000), "just now"], [iso(20_000), "just now"],
    [iso(-60_000), "1 min ago"], [iso(-59 * 60_000), "59 min ago"], [iso(-60 * 60_000), "1 h ago"],
    [iso(-47 * 3_600_000), "47 h ago"], [iso(-48 * 3_600_000), "2 d ago"], [iso(-72 * 3_600_000), "3 d ago"]
  ])("%s reads %s", (value, text) => {
    expect(relative(value)).toBe(text);
  });
});

describe("the small words", () => {
  it("names which kind of title an indexer serves", () => {
    expect(scopeLabel("movies")).toBe("Movies");
    expect(scopeLabel("tv")).toBe("TV");
    expect(scopeLabel("both")).toBe("Movies · TV");
    expect(scopeLabel(null)).toBe("Movies · TV");
    expect(scopeLabel(undefined)).toBe("Movies · TV");
  });

  it("names a protocol by a client's or an indexer's label, or as it is", () => {
    expect(protocolLabel("qbittorrent")).toBe("qBittorrent");
    expect(protocolLabel("newznab")).toBe("Newznab");
    expect(protocolLabel("mystery")).toBe("mystery");
  });

  it("reads the host of an address in lower case, or nothing for a bad one", () => {
    expect(indexerHost("https://API.Example.com:8080/path")).toBe("api.example.com");
    expect(indexerHost("not a url")).toBeNull();
    expect(indexerHost("")).toBeNull();
  });

  it("says seconds in words, rounding up", () => {
    expect(formatSeconds(0)).toBe("less than a second");
    expect(formatSeconds(0.9)).toBe("less than a second");
    expect(formatSeconds(1)).toBe("1 second");
    expect(formatSeconds(1.2)).toBe("2 seconds");
    expect(formatSeconds(30)).toBe("30 seconds");
  });
});

describe("a call cap that has been reached", () => {
  const status = (overrides: Record<string, unknown> = {}) => ({ allowed: false, limit: 100, count: 100, resetsAtUtc: "2026-11-05T10:00:00Z", unit: "day", ...overrides });
  const time = new Date("2026-11-05T10:00:00Z").toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

  it("says the cap, its unit and when it resets", () => {
    expect(describeCallLimitStatus("Search", status())).toBe(`Search limit reached (100 per day). Resets at ${time}.`);
    expect(describeCallLimitStatus("Grab", status({ unit: "hour", limit: 5 }))).toBe(`Grab limit reached (5 per hour). Resets at ${time}.`);
    expect(describeCallLimitStatus("Grab", status({ unit: null }))).toBe(`Grab limit reached (100 per day). Resets at ${time}.`);
  });

  it("says nothing when there is no cap, it has not been reached, or no reset time is known", () => {
    expect(describeCallLimitStatus("Search", null)).toBeNull();
    expect(describeCallLimitStatus("Search", undefined)).toBeNull();
    expect(describeCallLimitStatus("Search", status({ limit: null }))).toBeNull();
    expect(describeCallLimitStatus("Search", status({ allowed: true }))).toBeNull();
    expect(describeCallLimitStatus("Search", status({ resetsAtUtc: null }))).toBeNull();
    expect(describeCallLimitStatus("Search", status({ limit: 0 }))).toContain("0 per day");
  });
});

describe("the preferred flags field", () => {
  it("splits on commas, trimming and dropping blanks", () => {
    expect(splitIndexerFlags(" Freeleech , ,Internal,, ")).toEqual(["Freeleech", "Internal"]);
    expect(splitIndexerFlags("")).toEqual([]);
  });

  it("knows a flag that is already there, whatever its case", () => {
    expect(indexerFlagSelected("Freeleech, Internal", "internal")).toBe(true);
    expect(indexerFlagSelected("Freeleech", "Internal")).toBe(false);
    expect(indexerFlagSelected("", "Internal")).toBe(false);
  });

  it("adds a flag at the end, or removes it whatever its case, leaving the others as typed", () => {
    expect(toggleIndexerFlag("Freeleech", "Internal")).toBe("Freeleech, Internal");
    expect(toggleIndexerFlag("", "Internal")).toBe("Internal");
    expect(toggleIndexerFlag("Freeleech, Internal, Custom", "internal")).toBe("Freeleech, Custom");
    expect(toggleIndexerFlag("Internal", "Internal")).toBe("");
  });
});

describe("what the Indexers header counts", () => {
  const ix = (id: string, healthStatus: string, isEnabled = true) => ({ id, healthStatus, isEnabled });
  const used = () => 1;

  it("counts the ready ones and names every group left out, in order", () => {
    const result = indexerReadiness([ix("a", "healthy"), ix("b", "degraded"), ix("c", "unhealthy"), ix("d", "untested"), ix("e", "healthy", false), ix("f", "circuitOpen"), ix("g", "healthy")], (id) => (id === "g" ? 0 : 1), 2);
    expect(result).toEqual({ ready: 1, total: 7, notReady: ["1 degraded", "2 not answering", "1 not tested yet", "1 not used by any library", "1 paused"] });
  });

  it("does not count a healthy indexer as unused when there are no libraries", () => {
    expect(indexerReadiness([ix("a", "healthy")], () => 0, 0)).toEqual({ ready: 1, total: 1, notReady: [] });
  });

  it("counts a healthy indexer a library searches as ready, and is empty for none", () => {
    expect(indexerReadiness([ix("a", "healthy")], used, 3)).toEqual({ ready: 1, total: 1, notReady: [] });
    expect(indexerReadiness([], used, 3)).toEqual({ ready: 0, total: 0, notReady: [] });
  });

  it("counts a paused indexer as paused whatever its health", () => {
    expect(indexerReadiness([ix("a", "degraded", false)], used, 1).notReady).toEqual(["1 paused"]);
  });
});

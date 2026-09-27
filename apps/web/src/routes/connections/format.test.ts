import { describe, expect, it } from "vitest";
import { healthChip, indexerFlagSelected, splitIndexerFlags, toggleIndexerFlag, vipWarningActive } from "./format";

// #812: the client itself reporting paused/standby has to read as "Paused",
// not the generic unhealthy/idle status Deluno shows when it simply cannot
// reach the client.
describe("healthChip client-paused state (#812)", () => {
  const item = { isEnabled: true, healthStatus: "healthy" };

  it("shows Paused when the client itself reports it is paused", () => {
    expect(healthChip(item, true)).toEqual({ tone: "info", label: "Paused" });
  });

  it("shows the ordinary health chip when the client is not paused", () => {
    expect(healthChip(item, false)).toEqual({ tone: "ok", label: "Healthy" });
  });

  it("Off still wins over a stale paused reading for a disabled client", () => {
    expect(healthChip({ ...item, isEnabled: false }, true)).toEqual({ tone: "idle", label: "Off" });
  });
});

describe("indexer flag helpers (#745)", () => {
  it("splits and trims a comma-separated flag list", () => {
    expect(splitIndexerFlags(" freeleech ,  double upload,")).toEqual(["freeleech", "double upload"]);
    expect(splitIndexerFlags("")).toEqual([]);
  });

  it("finds a flag regardless of case", () => {
    expect(indexerFlagSelected("Freeleech, Internal", "freeleech")).toBe(true);
    expect(indexerFlagSelected("Freeleech, Internal", "Half Leech")).toBe(false);
  });

  it("adds a flag that isn't already selected", () => {
    expect(toggleIndexerFlag("", "Freeleech")).toBe("Freeleech");
    expect(toggleIndexerFlag("Freeleech", "Internal")).toBe("Freeleech, Internal");
  });

  it("removes a flag that is already selected, leaving the rest untouched", () => {
    expect(toggleIndexerFlag("Freeleech, Internal", "freeleech")).toBe("Internal");
  });

  it("keeps a custom typed flag the known list doesn't know about", () => {
    expect(toggleIndexerFlag("custom-site-flag", "Freeleech")).toBe("custom-site-flag, Freeleech");
  });
});

// #747: VIP/subscription expiry warns starting 7 days before the recorded
// date and continues after it lapses, until acknowledged or the date changes.
describe("VIP expiry warning (#747)", () => {
  it("does not warn when nothing is recorded", () => {
    expect(vipWarningActive({ vipExpiresUtc: null })).toBe(false);
  });

  it("does not warn more than 7 days before the recorded date", () => {
    const farFuture = new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString();
    expect(vipWarningActive({ vipExpiresUtc: farFuture })).toBe(false);
  });

  it("warns within the 7-day window before the recorded date", () => {
    const soon = new Date(Date.now() + 3 * 24 * 60 * 60 * 1000).toISOString();
    expect(vipWarningActive({ vipExpiresUtc: soon })).toBe(true);
  });

  it("keeps warning after the recorded date has lapsed", () => {
    const past = new Date(Date.now() - 1 * 24 * 60 * 60 * 1000).toISOString();
    expect(vipWarningActive({ vipExpiresUtc: past })).toBe(true);
  });

  it("clears once acknowledged for that exact date", () => {
    const soon = new Date(Date.now() + 3 * 24 * 60 * 60 * 1000).toISOString();
    expect(vipWarningActive({ vipExpiresUtc: soon, vipWarningAcknowledgedExpiryUtc: soon })).toBe(false);
  });

  it("warns again once the date is updated to a new value that itself falls in the window", () => {
    const soon = new Date(Date.now() + 3 * 24 * 60 * 60 * 1000).toISOString();
    const renewedButStillSoon = new Date(Date.now() + 2 * 24 * 60 * 60 * 1000).toISOString();
    expect(vipWarningActive({ vipExpiresUtc: renewedButStillSoon, vipWarningAcknowledgedExpiryUtc: soon })).toBe(true);
  });

  it("surfaces as a Status chip alongside a healthy indexer", () => {
    const soon = new Date(Date.now() + 3 * 24 * 60 * 60 * 1000).toISOString();
    const chip = healthChip({ isEnabled: true, healthStatus: "healthy", vipExpiresUtc: soon });
    expect(chip.tone).toBe("warn");
  });
});

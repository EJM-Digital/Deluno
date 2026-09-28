import type { ChipProps } from "../../components/ui/chip";
import { statusPresentation } from "../../lib/status-tones";
import { CLIENT_PRESETS, INDEXER_PRESETS } from "./presets";

/**
 * A source or client that is switched on and used by no library.
 *
 * Each library searches only the sources, and sends only to the clients, that
 * Library Routing lists for it, and adding a connection routes it nowhere. The
 * rig's Usenet source and SABnzbd read "Healthy" for three days while no library
 * used either: 56 of the soak's 73 expected grabs could not have happened.
 * "Healthy" beside something nothing uses reads as working.
 */
export function unusedChip(item: { isEnabled: boolean; healthStatus: string }, usedByCount: number, libraryCount: number): { tone: NonNullable<ChipProps["tone"]>; label: string } | null {
  // Only over "Healthy": a connection that is failing should say so first.
  if (!item.isEnabled || item.healthStatus !== "healthy" || libraryCount === 0 || usedByCount > 0) return null;
  return statusPresentation("connection.notRouted");
}

/**
 * Whether a recorded VIP/subscription expiry (#747) should currently warn:
 * starting 7 days before the date and continuing after it lapses, until the
 * user acknowledges that exact date or the date itself changes. Mirrors
 * IndexerVipWarning.IsActive on the backend.
 */
export function vipWarningActive(item: { vipExpiresUtc?: string | null; vipWarningAcknowledgedExpiryUtc?: string | null }): boolean {
  if (!item.vipExpiresUtc) return false;
  if (item.vipWarningAcknowledgedExpiryUtc === item.vipExpiresUtc) return false;
  const warnsFrom = new Date(item.vipExpiresUtc).getTime() - 7 * 24 * 60 * 60 * 1000;
  return Date.now() >= warnsFrom;
}

export function healthChip(item: { isEnabled: boolean; healthStatus: string; rateLimitedUntilUtc?: string | null; vipExpiresUtc?: string | null; vipWarningAcknowledgedExpiryUtc?: string | null }, clientPaused?: boolean): { tone: NonNullable<ChipProps["tone"]>; label: string } {
  if (!item.isEnabled) return statusPresentation("connection.off");
  // #812: the client itself reports it is paused/on standby — say that
  // instead of a generic unhealthy/idle status; nothing here calls for the
  // usual "check the config" response.
  if (clientPaused) return statusPresentation("connection.clientPaused");
  // Deluno backed off deliberately and resumes on its own, so this is motion,
  // not something a person has to deal with. It was amber.
  if (item.rateLimitedUntilUtc && new Date(item.rateLimitedUntilUtc).getTime() > Date.now()) return statusPresentation("connection.rateLimited");
  // #747: a VIP warning is a problem on this indexer specifically, same as
  // Degraded/Unhealthy — shown ahead of a plain Healthy chip, but a real
  // connection failure still takes precedence below.
  if (item.vipExpiresUtc && vipWarningActive(item) && (item.healthStatus === "healthy" || item.healthStatus === "untested")) {
    return new Date(item.vipExpiresUtc).getTime() <= Date.now()
      ? statusPresentation("connection.vipExpired")
      : statusPresentation("connection.vipExpiring");
  }
  switch (item.healthStatus) {
    case "healthy": return statusPresentation("connection.healthy");
    case "degraded": return statusPresentation("connection.degraded");
    case "untested": return statusPresentation("connection.untested");
    // Deluno stopped asking it after repeated failures (see lastHealthMessage
    // for when it tries again). Distinct from Degraded because the action is
    // different: Degraded means check the config, this means Reset it.
    case "circuitOpen": return statusPresentation("connection.circuitOpen");
    default: return statusPresentation("connection.unhealthy");
  }
}
export function relative(iso: string | null | undefined) { if (!iso) return "Never"; const minutes = Math.round(Math.abs(Date.now() - new Date(iso).getTime()) / 60000); return minutes < 1 ? "just now" : minutes < 60 ? `${minutes} min ago` : minutes < 60 * 48 ? `${Math.round(minutes / 60)} h ago` : `${Math.round(minutes / 1440)} d ago`; }
export function scopeLabel(scope: string | null | undefined) { return scope === "movies" ? "Movies" : scope === "tv" ? "TV" : "Movies · TV"; }
export function protocolLabel(protocol: string) { return CLIENT_PRESETS.find((preset) => preset.protocol === protocol)?.label ?? INDEXER_PRESETS.find((preset) => preset.protocol === protocol)?.label ?? protocol; }
export function indexerHost(baseUrl: string) { try { return new URL(baseUrl).hostname.toLowerCase(); } catch { return null; } }
export function formatSeconds(seconds: number) { return seconds < 1 ? "less than a second" : `${Math.ceil(seconds)} second${seconds >= 1.01 ? "s" : ""}`; }

/**
 * Plain words for an indexer's query or grab cap once it has been reached
 * (#977), e.g. "Search limit reached (100 per day). Resets at 10:00." Null
 * when there is no cap, or the cap has not been reached.
 */
export function describeCallLimitStatus(label: "Search" | "Grab", status: { allowed: boolean; limit: number | null; count: number; resetsAtUtc: string | null; unit: string | null } | null | undefined): string | null {
  if (!status || status.limit == null || status.allowed || !status.resetsAtUtc) return null;
  const unit = status.unit === "hour" ? "hour" : "day";
  const time = new Date(status.resetsAtUtc).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return `${label} limit reached (${status.limit} per ${unit}). Resets at ${time}.`;
}

/** The comma-separated flags in a "preferred indexer flags" field, trimmed and emptied of blanks. */
export function splitIndexerFlags(preferIndexerFlags: string): string[] {
  return preferIndexerFlags.split(",").map((flag) => flag.trim()).filter(Boolean);
}

/** Whether `flag` (e.g. "Freeleech") is already one of the comma-separated preferred flags, regardless of case. */
export function indexerFlagSelected(preferIndexerFlags: string, flag: string): boolean {
  return splitIndexerFlags(preferIndexerFlags).some((existing) => existing.toLowerCase() === flag.toLowerCase());
}

/** Adds or removes `flag` from a "preferred indexer flags" field, leaving any other typed flags untouched (#745). */
export function toggleIndexerFlag(preferIndexerFlags: string, flag: string): string {
  const flags = splitIndexerFlags(preferIndexerFlags);
  const next = indexerFlagSelected(preferIndexerFlags, flag)
    ? flags.filter((existing) => existing.toLowerCase() !== flag.toLowerCase())
    : [...flags, flag];
  return next.join(", ");
}

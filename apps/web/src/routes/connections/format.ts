import type { ChipProps } from "../../components/ui/chip";
import { statusPresentation } from "../../lib/status-tones";
import { CLIENT_PRESETS, INDEXER_PRESETS } from "./presets";

export function healthChip(item: { isEnabled: boolean; healthStatus: string; rateLimitedUntilUtc?: string | null }): { tone: NonNullable<ChipProps["tone"]>; label: string } {
  if (!item.isEnabled) return statusPresentation("connection.off");
  // Deluno backed off deliberately and resumes on its own, so this is motion,
  // not something a person has to deal with. It was amber.
  if (item.rateLimitedUntilUtc && new Date(item.rateLimitedUntilUtc).getTime() > Date.now()) return statusPresentation("connection.rateLimited");
  switch (item.healthStatus) {
    case "healthy": return statusPresentation("connection.healthy");
    case "degraded": return statusPresentation("connection.degraded");
    case "untested": return statusPresentation("connection.untested");
    default: return statusPresentation("connection.unhealthy");
  }
}
export function relative(iso: string | null | undefined) { if (!iso) return "Never"; const minutes = Math.round(Math.abs(Date.now() - new Date(iso).getTime()) / 60000); return minutes < 1 ? "just now" : minutes < 60 ? `${minutes} min ago` : minutes < 60 * 48 ? `${Math.round(minutes / 60)} h ago` : `${Math.round(minutes / 1440)} d ago`; }
export function scopeLabel(scope: string | null | undefined) { return scope === "movies" ? "Movies" : scope === "tv" ? "TV" : "Movies · TV"; }
export function protocolLabel(protocol: string) { return CLIENT_PRESETS.find((preset) => preset.protocol === protocol)?.label ?? INDEXER_PRESETS.find((preset) => preset.protocol === protocol)?.label ?? protocol; }
export function indexerHost(baseUrl: string) { try { return new URL(baseUrl).hostname.toLowerCase(); } catch { return null; } }
export function formatSeconds(seconds: number) { return seconds < 1 ? "less than a second" : `${Math.ceil(seconds)} second${seconds >= 1.01 ? "s" : ""}`; }

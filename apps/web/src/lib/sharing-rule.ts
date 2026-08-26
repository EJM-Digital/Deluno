import type { PlatformSettingsSnapshot } from "./api";
import { STRICT_SHARING } from "../routes/connections/forms";

/**
 * The sharing rule, said out loud (#288).
 *
 * A user adding a search source is being asked whether that site is stricter
 * than their normal rule. They cannot answer that without being told what their
 * normal rule *is* — and sending them to another screen to find out is the
 * thing this whole feature exists to avoid.
 *
 * So both sides of the question state their consequence in the same breath, in
 * days and plain words rather than hours and ratios.
 */
export function describeGlobalSharingRule(settings: PlatformSettingsSnapshot): string {
  if (settings.sharingMode === "leave-alone") {
    return "Your normal rule: Deluno never removes anything from your download client.";
  }

  if (settings.sharingMode === "tidy-now") {
    return "Your normal rule: Deluno reclaims the space as soon as the import is verified.";
  }

  const targets = [
    settings.sharingForHours ? `for ${describeHours(settings.sharingForHours)}` : null,
    settings.sharingUntilRatio ? `until ratio ${settings.sharingUntilRatio.toFixed(1)}` : null
  ].filter(Boolean);

  return targets.length
    ? `Your normal rule: keep sharing ${targets.join(" and ")}, then reclaim the space.`
    : "Your normal rule: reclaim the space as soon as the import is verified.";
}

/**
 * What choosing "strict" actually commits to. Written as the obligation the
 * site imposes rather than as the settings it writes, because "ratio 1.0" means
 * nothing to someone joining their first tracker.
 */
export function describeStrictSharingRule(): string {
  return `Deluno keeps sharing for ${describeHours(STRICT_SHARING.forHours)}, and until you have given back as much as you took. It never stops early on its own.`;
}

/** Hours as a person says them: "3 days", "14 days", "6 hours". */
function describeHours(hours: number): string {
  if (hours >= 48) return `${Math.round(hours / 24)} days`;
  if (hours >= 24) return "1 day";
  if (hours === 1) return "1 hour";
  return `${hours} hours`;
}

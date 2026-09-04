import { fetchJson } from "./client";
import type {
  ReleasePreferencePlanCompilation,
  ReleasePreferencePlan,
  ReleasePreferenceRegistryResponse,
  ReleasePreferencePreviewResponse,
  StoredReleasePreferencePlan
} from "./types";

const RELEASE_PREFERENCES_BASE = "/api/v1/release-preferences";

export function fetchPreferenceRegistry(mediaType?: string) {
  const query = mediaType ? `?mediaType=${encodeURIComponent(mediaType)}` : "";
  return fetchJson<ReleasePreferenceRegistryResponse>(`${RELEASE_PREFERENCES_BASE}/registry${query}`);
}

export function fetchStoredPreferencePlans(mediaType?: string) {
  const query = mediaType ? `?mediaType=${encodeURIComponent(mediaType)}` : "";
  return fetchJson<StoredReleasePreferencePlan[]>(`${RELEASE_PREFERENCES_BASE}/plans${query}`);
}

export function fetchStoredPreferencePlan(planId: string, version?: string) {
  const query = version ? `?version=${encodeURIComponent(version)}` : "";
  return fetchJson<StoredReleasePreferencePlan>(
    `${RELEASE_PREFERENCES_BASE}/plans/${encodeURIComponent(planId)}${query}`
  );
}

export function savePreferencePlan(plan: ReleasePreferencePlan) {
  return fetchJson<StoredReleasePreferencePlan>(`${RELEASE_PREFERENCES_BASE}/plans`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(plan)
  });
}

export function previewReleasePreference(request: {
  planId: string;
  planVersion?: string;
  releaseName: string;
  currentReleaseName?: string;
  candidateQuality?: string;
  currentQuality?: string;
  seeders?: number;
}) {
  return fetchJson<ReleasePreferencePreviewResponse>(`${RELEASE_PREFERENCES_BASE}/preview`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

export function compileQualityProfilePreferences(profileId: string) {
  return fetchJson<ReleasePreferencePlanCompilation>(
    `${RELEASE_PREFERENCES_BASE}/plans/quality-profile/${encodeURIComponent(profileId)}`
  );
}

/**
 * How a profile nobody has saved yet would judge one release.
 *
 * <p>The preview above needs a persisted plan id, which is exactly what a
 * half-answered profile does not have. This compiles the answers as they stand,
 * uses the plan once and drops it — nothing is written.</p>
 */
export function judgeDraftProfile(request: {
  name: string;
  mediaType: string;
  allowedQualities: string[];
  cutoffQuality: string;
  customFormatIds: string[];
  formatIntents: Record<string, string>;
  upgradeUntilCutoff: boolean;
  upgradeUnknownItems: boolean;
  allowLowerQualityReplacements: boolean;
  releaseName: string;
  currentReleaseName?: string;
}) {
  return fetchJson<DraftProfileJudgement>(`${RELEASE_PREFERENCES_BASE}/judge-draft`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

export interface DraftProfileJudgement {
  releaseName: string;
  /**
   * Why the profile's allowed tiers refuse this outright, or null. A gate
   * rather than a preference, so it outranks whatever the evaluation says.
   */
  refusal?: string | null;
  candidateEvaluation?: {
    status?: string;
    hardGatesPassed?: boolean;
    targetsMet?: boolean;
    reasons?: string[];
  };
}

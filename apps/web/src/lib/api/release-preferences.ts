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

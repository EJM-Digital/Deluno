import { fetchJson } from "./client";
import type {
  GuidePackage,
  GuidePackageUpdatePreview,
  GuidePackageUpdateRequest,
  GuidePackageSyncRequest,
  GuideCapabilityInventory,
  GuideUpdateCheckState,
  StoredGuidePackage
} from "./types";

/** Reads the server-owned, versioned guide package used by quality screens. */
export function fetchTrashGuidePackage(): Promise<GuidePackage> {
  return fetchJson<GuidePackage>("/api/v1/guides/trash/package");
}

export function fetchTrashGuideVersions(): Promise<StoredGuidePackage[]> {
  return fetchJson<StoredGuidePackage[]>("/api/v1/guides/trash/versions");
}

/**
 * Makes a retained guide version current again.
 *
 * Every version is immutable and kept, which is what makes each update a
 * rollback point — but a point with no way back to it is not one (#350).
 */
export function activateTrashGuideVersion(version: number, packageId?: string): Promise<StoredGuidePackage> {
  const query = packageId ? `?packageId=${encodeURIComponent(packageId)}` : "";
  return fetchJson<StoredGuidePackage>(`/api/v1/guides/trash/versions/${version}/activate${query}`, {
    method: "POST"
  });
}

export function fetchTrashGuideInventory(): Promise<GuideCapabilityInventory> {
  return fetchJson<GuideCapabilityInventory>("/api/v1/guides/trash/inventory");
}

/** Reads the owner-controlled, report-only upstream guide check state. */
export function fetchTrashGuideUpdateCheck(): Promise<GuideUpdateCheckState> {
  return fetchJson<GuideUpdateCheckState>("/api/v1/guides/trash/update-check");
}

export function setTrashGuideUpdateCheckEnabled(isEnabled: boolean): Promise<GuideUpdateCheckState> {
  return fetchJson<GuideUpdateCheckState>("/api/v1/guides/trash/update-check/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ isEnabled })
  });
}

export function runTrashGuideUpdateCheck(): Promise<GuideUpdateCheckState> {
  return fetchJson<GuideUpdateCheckState>("/api/v1/guides/trash/update-check/run", {
    method: "POST"
  });
}

export function previewTrashGuideUpdate(request: GuidePackageUpdateRequest): Promise<GuidePackageUpdatePreview> {
  return fetchJson<GuidePackageUpdatePreview>("/api/v1/guides/trash/preview", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

export function applyTrashGuideUpdate(request: GuidePackageUpdateRequest): Promise<StoredGuidePackage> {
  return fetchJson<StoredGuidePackage>("/api/v1/guides/trash/apply", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

/** Stages an exact upstream revision as a validated package diff; it does not persist anything. */
export function previewTrashGuideSync(request: GuidePackageSyncRequest): Promise<GuidePackageUpdatePreview> {
  return fetchJson<GuidePackageUpdatePreview>("/api/v1/guides/trash/sync/preview", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

/** Applies precisely the upstream candidate that was previewed. */
export function applyTrashGuideSync(request: GuidePackageSyncRequest): Promise<StoredGuidePackage> {
  return fetchJson<StoredGuidePackage>("/api/v1/guides/trash/sync/apply", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
}

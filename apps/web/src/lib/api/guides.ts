import { fetchJson } from "./client";
import type {
  GuidePackage,
  GuidePackageUpdatePreview,
  GuidePackageUpdateRequest,
  GuideCapabilityInventory,
  StoredGuidePackage
} from "./types";

/** Reads the server-owned, versioned guide package used by quality screens. */
export function fetchTrashGuidePackage(): Promise<GuidePackage> {
  return fetchJson<GuidePackage>("/api/v1/guides/trash/package");
}

export function fetchTrashGuideVersions(): Promise<StoredGuidePackage[]> {
  return fetchJson<StoredGuidePackage[]>("/api/v1/guides/trash/versions");
}

export function fetchTrashGuideInventory(): Promise<GuideCapabilityInventory> {
  return fetchJson<GuideCapabilityInventory>("/api/v1/guides/trash/inventory");
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

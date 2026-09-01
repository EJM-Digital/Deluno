export type GuideMappingStatus = "reviewed" | "advanced";

export interface GuidePackage {
  id: string;
  name: string;
  version: number;
  schemaVersion: number;
  source: GuidePackageProvenance;
  integritySha256: string;
  qualityTiers: GuideQualityTier[];
  customFormats: GuideCustomFormat[];
  qualityProfiles: GuideQualityProfile[];
  bundles: GuideFormatBundle[];
  sourceInventory?: GuideSourceInventory | null;
}

export interface GuidePackageProvenance {
  sourceName: string;
  repositoryUrl: string;
  guideUrl: string;
  upstreamRevision: string;
  reviewedUtc: string;
  adaptation: string;
}

export interface GuideQualityTier {
  id: string;
  label: string;
  source: string;
  resolution: string;
  minMbPerMin: number;
  maxMbPerMin: number;
  rank: number;
}

export interface GuideCustomFormat {
  trashId: string;
  name: string;
  category: string;
  description: string;
  originalScore: number;
  patterns: string[];
  bundleOnly: boolean;
  mappingStatus: GuideMappingStatus;
  mappedTraitIds: string[];
  sourceKind: string;
  mediaTypes?: string[];
  sourceGroupIds?: string[];
  sourceMatcherClauses?: GuideSourceMatcherClause[];
  sourceScores?: Record<string, number>;
  sourcePath?: string;
}

export interface GuideSourceInventory {
  schemaVersion: number;
  upstreamRevision: string;
  customFormats: GuideSourceCustomFormat[];
  formatGroups: GuideSourceFormatGroup[];
  qualityProfiles: GuideSourceQualityProfile[];
}

export interface GuideSourceCustomFormat {
  trashId: string;
  name: string;
  description?: string | null;
  mediaType: string;
  sourcePath: string;
  sourceBlobSha: string;
  scores: Record<string, number>;
  includeWhenRenaming: boolean;
  matcherClauses: GuideSourceMatcherClause[];
}

export interface GuideSourceMatcherClause {
  name: string;
  implementation: string;
  negate: boolean;
  required: boolean;
  fieldsJson: string;
}

export interface GuideSourceFormatGroup {
  trashId: string;
  name: string;
  description?: string | null;
  mediaType: string;
  sourcePath: string;
  sourceBlobSha: string;
  customFormats: GuideSourceFormatGroupEntry[];
  qualityProfileIds: string[];
}

export interface GuideSourceFormatGroupEntry {
  trashId: string;
  name: string;
  required: boolean;
}

export interface GuideSourceQualityProfile {
  trashId: string;
  name: string;
  description?: string | null;
  mediaType: string;
  sourcePath: string;
  sourceBlobSha: string;
  formatAssignments: GuideSourceProfileFormatAssignment[];
  definitionJson: string;
}

export interface GuideSourceProfileFormatAssignment {
  name: string;
  trashId: string;
}

export interface GuideRecommendedFormat {
  trashId: string;
  score: number;
}

export interface GuideQualityProfile {
  id: string;
  name: string;
  tagline: string;
  description: string;
  highlights: string[];
  mediaType: string;
  qualityOrder: string[];
  cutoffQualityId: string;
  upgradeAllowed: boolean;
  minFormatScore: number;
  cutoffFormatScore: number;
  recommendedFormats: GuideRecommendedFormat[];
}

export interface GuideFormatBundleEntry {
  trashId: string;
  score: number | null;
}

export interface GuideFormatBundle {
  id: string;
  name: string;
  level: string;
  mediaType: "movies" | "tv" | "all" | string;
  description: string;
  bestFor: string;
  includes: GuideFormatBundleEntry[];
  warnings: string[];
}

export interface StoredGuidePackage {
  package: GuidePackage;
  isActive: boolean;
  storedUtc: string;
  integritySha256: string;
}

export interface GuidePackageUpdateRequest {
  package: GuidePackage;
  expectedCurrentIntegritySha256?: string | null;
}

export interface GuideProfileUpdateDiff {
  profileId: string;
  profileName: string;
  currentPlanHash: string | null;
  proposedPlanHash: string | null;
  currentAdvancedRuleCount: number;
  proposedAdvancedRuleCount: number;
  changes: string[];
  warnings: string[];
}

export interface GuidePackageUpdatePreview {
  current: StoredGuidePackage;
  proposed: GuidePackage;
  proposedIntegritySha256: string;
  proposedInventory: GuideCapabilityInventory;
  profileDiffs: GuideProfileUpdateDiff[];
  errors: string[];
  warnings: string[];
  canApply: boolean;
}

export interface GuideCapabilityInventoryItem {
  kind: string;
  id: string;
  mediaType: string;
  category: string;
  representation: string;
  typedTraitIds: string[];
  provenance: string;
}

export interface GuideCapabilityInventory {
  packageId: string;
  packageVersion: number;
  sourceRevision: string;
  packageIntegritySha256: string;
  totalItemCount: number;
  typedItemCount: number;
  advancedItemCount: number;
  items: GuideCapabilityInventoryItem[];
  unaccounted: string[];
  inventoryHash: string;
}

export interface GuideUpdateCheckState {
  isEnabled: boolean;
  lastCheckedUtc: string | null;
  lastSeenRevision: string | null;
  status: "disabled" | "never-checked" | "up-to-date" | "update-available" | "failed" | string;
  error: string | null;
  report: GuideUpdateCheckReport | null;
  updatedUtc: string;
}

export interface GuideUpdateCheckReport {
  baselineRevision: string;
  remoteRevision: string;
  checkedUtc: string;
  isComplete: boolean;
  changes: GuideUpdateCheckChange[];
  addedSources: GuideUpdateCheckAddedSource[];
  summary: string;
}

export interface GuideUpdateCheckChange {
  kind: string;
  id: string;
  name: string;
  mediaType: string;
  sourcePath: string;
  changeType: "changed" | "removed" | string;
  isInUse: boolean;
  inUseCustomFormatIds: string[];
}

export interface GuideUpdateCheckAddedSource {
  kind: string;
  mediaType: string;
  sourcePath: string;
}

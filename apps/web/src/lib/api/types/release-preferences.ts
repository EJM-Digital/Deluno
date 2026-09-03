export type PreferenceIntent = "required" | "forbidden" | "ranked" | "tieBreak" | "neutral";
export type PreferenceFactState = "present" | "absent" | "unknown" | "conflicting";
export type PreferenceEvaluationStatus = "missing" | "needsReview" | "belowGoal" | "meetsPlan";
export type PreferenceCandidateStatus = "rejected" | "needsReview" | "acceptable" | "bestMatchNow" | "equivalent" | "currentBetter" | "upgrade";
export type PreferenceRelationshipKind = "implies" | "requires" | "subsumes" | "coreOf" | "carriedBy" | "incompatible";
export type PreferenceEvidenceModel = "openWorld" | "closedWorld";

export interface PreferenceTraitDefinition {
  id: string;
  dimension: string;
  displayName: string;
  aliases: string[] | null;
  mediaTypes: string[] | null;
  guideSource: string | null;
  guideVersion: string | null;
  transient: boolean;
}

export interface PreferenceRelationship {
  fromTraitId: string;
  toTraitId: string;
  kind: PreferenceRelationshipKind;
}

/** One complete device/source compatibility path. Traits in an alternative
 * are ANDed; alternatives are ORed; groups are ANDed. */
export interface PreferenceCompatibilityGroup {
  id: string;
  alternatives: string[][];
}

export interface PreferenceEvidence {
  source: string;
  confidence: number | null;
  detail: string | null;
  detectionRule: string | null;
  detectionVersion: string | null;
  model: PreferenceEvidenceModel;
}

export interface PreferenceFact {
  traitId: string;
  state: PreferenceFactState;
  evidence: PreferenceEvidence | null;
}

export interface PreferenceFamilyLevel {
  id: string;
  rank: number;
  traitIds: string[];
}

export interface PreferenceFamily {
  id: string;
  dimension: string;
  order: number;
  intent: PreferenceIntent;
  levels: PreferenceFamilyLevel[];
  targetLevelId: string | null;
  upgradeDriving: boolean;
  transient: boolean;
}

export interface PreferencePlanProvenance {
  sourceKind: string;
  sourceId: string;
  sourceVersion: string;
  originalScore: string | null;
  assignedScore: string | null;
  mappingId: string | null;
  mappingVersion: string | null;
  layer: string | null;
  /**
   * The matcher this source was compiled from, and the traits it produced.
   * Without them a plan can say a preference came from a rule but not what
   * the rule was, which is not a trace anybody can follow.
   */
  matcherDefinition: string | null;
  mappedTraitIds: string[] | null;
  matcherAny: boolean;
}

export interface ReleasePreferencePlan {
  id: string;
  version: string;
  mediaType: string;
  families: PreferenceFamily[];
  requiredTraitIds: string[] | null;
  forbiddenTraitIds: string[] | null;
  relationships: PreferenceRelationship[] | null;
  dimensionOrder: string[] | null;
  compatibilityScope: string | null;
  scenario: string | null;
  provenance: string | null;
  overrides: Record<string, string> | null;
  sources: PreferencePlanProvenance[] | null;
  requiredAnyTraitGroups: string[][] | null;
  compatibilityGroups: PreferenceCompatibilityGroup[] | null;
}

export interface StoredReleasePreferencePlan {
  plan: ReleasePreferencePlan;
  planHash: string;
  createdUtc: string;
}

export interface ReleasePreferenceRegistryResponse {
  version: string;
  mediaType: string;
  traits: PreferenceTraitDefinition[];
  relationships: PreferenceRelationship[];
}

export type LegacyPreferenceRuleKind =
  | "exactTyped"
  | "guideMapped"
  | "orderedFamilyCandidate"
  | "hardGateCandidate"
  | "tieBreakCandidate"
  | "ambiguousOverlap"
  | "conflicting"
  | "unmappedAdvanced"
  | "invalid";

export interface LegacyPreferenceRuleTranslation {
  ruleId: string;
  name: string;
  trashId: string | null;
  mediaType: string;
  originalScore: number;
  upgradeAllowed: boolean;
  conditions: string;
  kind: LegacyPreferenceRuleKind;
  proposedIntent: PreferenceIntent | null;
  requiresReview: boolean;
  explanation: string;
}

export interface ReleasePreferencePlanCompilation {
  registryVersion: string;
  profileId: string;
  profileName: string;
  plan: ReleasePreferencePlan;
  planHash: string;
  advancedRules: LegacyPreferenceRuleTranslation[];
  warnings: string[];
  requiresReview: boolean;
  storedUtc: string;
}

export interface PreferenceFamilyEvaluation {
  familyId: string;
  intent: PreferenceIntent;
  state: PreferenceFactState;
  selectedLevelId: string | null;
  selectedRank: number;
  targetLevelId: string | null;
  targetMet: boolean;
  upgradeDriving: boolean;
  transient: boolean;
  explanation: string;
}

export interface PreferenceEvaluation {
  planId: string;
  planVersion: string;
  planHash: string;
  status: PreferenceEvaluationStatus;
  hardGatesPassed: boolean;
  targetsMet: boolean;
  families: PreferenceFamilyEvaluation[];
  reasons: string[];
}

export interface PreferenceComparison {
  planId: string;
  planVersion: string;
  planHash: string;
  status: PreferenceCandidateStatus;
  persistentImprovement: boolean;
  regressed: boolean;
  equivalent: boolean;
  decisiveFamilyId: string | null;
  persistentImprovementFamilyId?: string | null;
  reasons: string[];
  current: PreferenceEvaluation;
  candidate: PreferenceEvaluation;
}

export interface ReleasePreferencePreviewResponse {
  releaseName: string;
  planId: string;
  planVersion: string;
  planHash: string;
  candidateFacts: PreferenceFact[];
  candidateEvaluation: PreferenceEvaluation;
  currentReleaseName: string | null;
  currentFacts: PreferenceFact[] | null;
  currentEvaluation: PreferenceEvaluation | null;
  comparison: PreferenceComparison | null;
}

export interface PreferenceEvaluationSnapshot {
  mediaId: string;
  libraryId: string | null;
  fileIdentity: string;
  filePath: string | null;
  fileSizeBytes: number | null;
  planId: string;
  planVersion: string;
  planHash: string;
  facts: PreferenceFact[];
  evaluation: PreferenceEvaluation;
  matchedRuleIds: string[];
  evaluatedUtc: string;
  source: string | null;
}

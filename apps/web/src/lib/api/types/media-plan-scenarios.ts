export interface MediaPlanScenarioVariant {
  mediaType: string;
  qualityPresetId: string;
  sizeTierId: string;
  sizeTierName: string;
  sizeDescription: string;
  searchIntervalHours: number;
  retryDelayHours: number;
  upgradeUntilCutoff: boolean;
  subtitleIntent: string;
  routingIntent: string;
  sharingIntent: string;
  cleanupIntent: string;
  notificationIntent: string;
  namingIntent: string;
  summary: string;
}

export interface MediaPlanScenario {
  id: string;
  name: string;
  description: string;
  mediaTypes: string[];
  requirements: string[];
  version: number;
  variants: MediaPlanScenarioVariant[];
}

export interface MediaPlanScenarioBehavior {
  id: string;
  area: string;
  intent: string;
  applicationStatus: "applied" | "requires-configuration" | "informational" | string;
  explanation: string;
  configurationSurface: string | null;
}

export interface MediaPlanScenarioCompilation {
  scenarioId: string;
  scenarioVersion: number;
  scenarioName: string;
  mediaType: string;
  qualityPresetId: string;
  variant: MediaPlanScenarioVariant;
  policySet: {
    name: string;
    mediaType: string;
    qualityProfileId: string | null;
    destinationRuleId: string | null;
    customFormatIds: string | null;
    searchIntervalOverrideHours: number | null;
    retryDelayOverrideHours: number | null;
    upgradeUntilCutoff: boolean;
    isEnabled: boolean;
    notes: string | null;
    automationIntent: MediaPlanAutomationIntent | null;
  };
  includedBehaviors: string[];
  requirements: string[];
  summary: string;
  behaviors?: MediaPlanScenarioBehavior[];
}
import type { MediaPlanAutomationIntent } from "./resources";

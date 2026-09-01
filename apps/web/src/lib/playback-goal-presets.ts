export type PlaybackGoalPresetId = "everywhere" | "main" | "lossless" | "atmos" | "storage";

export interface PlaybackGoalPreset {
  name: string;
  mustPlay: boolean;
  preferredTraitIds: string[];
  stopWhenTraitId: string;
  missingTraitIds: string[];
}

interface PlaybackGoalPresetDefinition {
  name: string;
  mustPlay: boolean;
  preferredTraitIds: string[];
  stopWhenTraitId: string;
}

const PRESETS: Record<PlaybackGoalPresetId, PlaybackGoalPresetDefinition> = {
  everywhere: {
    name: "Works everywhere",
    mustPlay: true,
    preferredTraitIds: [],
    stopWhenTraitId: ""
  },
  main: {
    name: "Best for my main setup",
    mustPlay: false,
    preferredTraitIds: [],
    stopWhenTraitId: ""
  },
  lossless: {
    name: "Best lossless audio",
    mustPlay: false,
    preferredTraitIds: [
      "audio.format.truehd-atmos",
      "audio.format.dtsx",
      "audio.format.truehd",
      "audio.format.dts-hd-ma"
    ],
    stopWhenTraitId: "audio.format.truehd"
  },
  atmos: {
    name: "Atmos preferred",
    mustPlay: false,
    preferredTraitIds: [
      "audio.format.truehd-atmos",
      "audio.format.eac3-atmos",
      "audio.format.truehd"
    ],
    stopWhenTraitId: "audio.format.eac3-atmos"
  },
  storage: {
    name: "Storage balanced",
    mustPlay: false,
    preferredTraitIds: [],
    stopWhenTraitId: ""
  }
};

/**
 * Resolves a friendly preset against the registry returned by the API. Missing
 * registry traits are reported explicitly so a preset cannot silently compile
 * to a weaker ladder after an identifier changes.
 */
export function resolvePlaybackGoalPreset(
  presetId: PlaybackGoalPresetId,
  availableTraitIds: Iterable<string>
): PlaybackGoalPreset {
  const definition = PRESETS[presetId];
  const available = new Set(availableTraitIds);
  const referenced = [
    ...definition.preferredTraitIds,
    ...(definition.stopWhenTraitId ? [definition.stopWhenTraitId] : [])
  ];
  const missingTraitIds = [...new Set(referenced.filter((traitId) => !available.has(traitId)))];

  return {
    ...definition,
    preferredTraitIds: [...definition.preferredTraitIds],
    missingTraitIds
  };
}

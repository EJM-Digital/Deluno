import { describe, expect, it } from "vitest";
import { resolvePlaybackGoalPreset } from "./playback-goal-presets";

const canonicalAudioTraits = [
  "audio.format.truehd-atmos",
  "audio.format.dtsx",
  "audio.format.truehd",
  "audio.format.dts-hd-ma",
  "audio.format.eac3-atmos"
];

describe("playback goal presets", () => {
  it("builds the complete lossless ladder from canonical registry ids", () => {
    const preset = resolvePlaybackGoalPreset("lossless", canonicalAudioTraits);

    expect(preset.preferredTraitIds).toEqual([
      "audio.format.truehd-atmos",
      "audio.format.dtsx",
      "audio.format.truehd",
      "audio.format.dts-hd-ma"
    ]);
    expect(preset.stopWhenTraitId).toBe("audio.format.truehd");
    expect(preset.missingTraitIds).toEqual([]);
  });

  it("builds the Atmos ladder with the canonical DD+ Atmos id", () => {
    const preset = resolvePlaybackGoalPreset("atmos", canonicalAudioTraits);

    expect(preset.preferredTraitIds).toEqual([
      "audio.format.truehd-atmos",
      "audio.format.eac3-atmos",
      "audio.format.truehd"
    ]);
    expect(preset.stopWhenTraitId).toBe("audio.format.eac3-atmos");
    expect(preset.missingTraitIds).toEqual([]);
  });

  it("reports a registry mismatch instead of silently shortening a preset", () => {
    const preset = resolvePlaybackGoalPreset(
      "atmos",
      canonicalAudioTraits.filter((traitId) => traitId !== "audio.format.eac3-atmos")
    );

    expect(preset.missingTraitIds).toEqual(["audio.format.eac3-atmos"]);
  });
});

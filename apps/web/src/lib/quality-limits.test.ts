import { describe, expect, it } from "vitest";
import {
  mapCutoffScore,
  mapUpgradeGainScore,
  mapMinScore,
  nearestCutoffChoice,
  nearestUpgradeGainChoice,
  CUTOFF_LABELS,
  UPGRADE_GAIN_LABELS
} from "./quality-limits";

describe("quality-limits", () => {
  it("maps every cutoff choice between the guide's floor and ceiling", () => {
    expect(mapCutoffScore("good", 0, 10000)).toBe(2500);
    expect(mapCutoffScore("great", 0, 10000)).toBe(6000);
    expect(mapCutoffScore("best", 0, 10000)).toBe(10000);
  });

  it("orders cutoff choices good to best", () => {
    const good = mapCutoffScore("good", 0, 10000);
    const great = mapCutoffScore("great", 0, 10000);
    const best = mapCutoffScore("best", 0, 10000);
    expect(good).toBeLessThan(great);
    expect(great).toBeLessThan(best);
  });

  it("scales every upgrade-gain choice off the guide's own minimum", () => {
    expect(mapUpgradeGainScore("aLittle", 1)).toBe(1);
    expect(mapUpgradeGainScore("clearly", 1)).toBe(3);
    expect(mapUpgradeGainScore("much", 1)).toBe(6);
  });

  it("never grab bad off means no minimum", () => {
    expect(mapMinScore(false, 550)).toBeNull();
  });

  it("never grab bad on takes the guide's own minimum", () => {
    expect(mapMinScore(true, 550)).toBe(550);
  });

  it("rounds a stored cutoff number back to its plain level", () => {
    expect(nearestCutoffChoice(2500, 0, 10000)).toBe("good");
    expect(nearestCutoffChoice(6000, 0, 10000)).toBe("great");
    expect(nearestCutoffChoice(10000, 0, 10000)).toBe("best");
  });

  it("rounds a stored upgrade-gain number back to its plain level", () => {
    expect(nearestUpgradeGainChoice(1, 1)).toBe("aLittle");
    expect(nearestUpgradeGainChoice(3, 1)).toBe("clearly");
    expect(nearestUpgradeGainChoice(6, 1)).toBe("much");
  });

  it("labels are plain words, not numbers", () => {
    expect(CUTOFF_LABELS.good).toBe("Good");
    expect(CUTOFF_LABELS.best).toBe("The best available");
    expect(UPGRADE_GAIN_LABELS.clearly).toBe("Clearly better");
  });
});

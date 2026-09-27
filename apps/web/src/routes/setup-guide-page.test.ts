import { describe, expect, it } from "vitest";
import {
  AUTOMATION_QUESTIONS,
  buildAutomationSettingsPayload,
  buildIndexerAutomationFields,
  buildLibraryAutomationFields,
  getEffectiveFormatGoal,
  hasAnsweredAllAutomationQuestions,
  resolveDraftFormatGoal,
  type AutomationChoices
} from "./setup-guide-page";

const UNANSWERED: AutomationChoices = {
  searchMissingTitles: null,
  rssGrabs: null,
  qualityUpgrades: null,
  renameOnImport: null,
  subtitleFetching: null,
  removeBadDownloads: null,
  deleteBadDownloadFiles: null,
  leftoverCleanup: null,
  leftoverDownloadCleanup: null
};

const ALL_NO: AutomationChoices = {
  searchMissingTitles: false,
  rssGrabs: false,
  qualityUpgrades: false,
  renameOnImport: false,
  subtitleFetching: false,
  removeBadDownloads: false,
  deleteBadDownloadFiles: false,
  leftoverCleanup: false,
  leftoverDownloadCleanup: false
};

const ALL_YES: AutomationChoices = {
  searchMissingTitles: true,
  rssGrabs: true,
  qualityUpgrades: true,
  renameOnImport: true,
  subtitleFetching: true,
  removeBadDownloads: true,
  deleteBadDownloadFiles: true,
  leftoverCleanup: true,
  leftoverDownloadCleanup: true
};

describe("setup guide draft format goal (#731)", () => {
  it("keeps a saved anime draft instead of silently rewriting it to balanced", () => {
    expect(resolveDraftFormatGoal("anime")).toBe("anime");
  });

  it("round-trips every other goal the guide still offers", () => {
    expect(resolveDraftFormatGoal("simpleClean")).toBe("simpleClean");
    expect(resolveDraftFormatGoal("balanced")).toBe("balanced");
    expect(resolveDraftFormatGoal("homeTheater")).toBe("homeTheater");
    expect(resolveDraftFormatGoal("storageSaver")).toBe("storageSaver");
    expect(resolveDraftFormatGoal("archive")).toBe("archive");
  });

  it("falls back to not-chosen for an empty or unrecognised draft value", () => {
    expect(resolveDraftFormatGoal("")).toBe("");
    expect(resolveDraftFormatGoal("some-retired-goal")).toBe("");
  });
});

describe("setup guide effective format goal per media type", () => {
  type Form = Parameters<typeof getEffectiveFormatGoal>[1];
  it("gives TV the anime goal and films Balanced, because the anime bundle is TV-only", () => {
    const form = { formatGoal: "anime" } as Form;
    expect(getEffectiveFormatGoal("tv", form)).toBe("anime");
    expect(getEffectiveFormatGoal("movies", form)).toBe("balanced");
  });

  it("uses the chosen goal for both media types otherwise", () => {
    const form = { formatGoal: "homeTheater" } as Form;
    expect(getEffectiveFormatGoal("tv", form)).toBe("homeTheater");
    expect(getEffectiveFormatGoal("movies", form)).toBe("homeTheater");
  });

  it("gives films the archive goal and TV Balanced, because archive-4k-remux is movies-only (B006)", () => {
    const form = { formatGoal: "archive" } as Form;
    expect(getEffectiveFormatGoal("movies", form)).toBe("archive");
    expect(getEffectiveFormatGoal("tv", form)).toBe("balanced");
  });
});

describe("setup guide Automation step (#936)", () => {
  it("has nothing pre-selected: an unanswered set of choices is not complete", () => {
    expect(hasAnsweredAllAutomationQuestions(UNANSWERED)).toBe(false);
  });

  it("is not complete until every single question has an answer, even if only one is missing", () => {
    for (const item of AUTOMATION_QUESTIONS) {
      const almostAllAnswered: AutomationChoices = { ...ALL_NO, [item.key]: null };
      expect(hasAnsweredAllAutomationQuestions(almostAllAnswered)).toBe(false);
    }
  });

  it("is complete once every question has an explicit Yes or No, even if every answer is No", () => {
    expect(hasAnsweredAllAutomationQuestions(ALL_NO)).toBe(true);
    expect(hasAnsweredAllAutomationQuestions(ALL_YES)).toBe(true);
  });

  it("asks exactly the eight behaviours #936 lists plus #932's leftover-download cleanup, each with its own key", () => {
    const keys = AUTOMATION_QUESTIONS.map((item) => item.key).sort();
    expect(keys).toEqual([
      "deleteBadDownloadFiles",
      "leftoverCleanup",
      "leftoverDownloadCleanup",
      "qualityUpgrades",
      "removeBadDownloads",
      "renameOnImport",
      "rssGrabs",
      "searchMissingTitles",
      "subtitleFetching"
    ]);
  });

  it("never hints that removing or deleting a bad download is recommended", () => {
    const removeQuestion = AUTOMATION_QUESTIONS.find((item) => item.key === "removeBadDownloads")!;
    const deleteQuestion = AUTOMATION_QUESTIONS.find((item) => item.key === "deleteBadDownloadFiles")!;
    for (const question of [removeQuestion, deleteQuestion]) {
      expect(question.question.toLowerCase()).not.toContain("recommended");
      expect(question.help.toLowerCase()).not.toContain("recommended");
    }
  });

  it("maps every 'No' answer to every automatic setting reading off", () => {
    expect(buildAutomationSettingsPayload(ALL_NO)).toEqual({
      autoStartJobs: false,
      renameOnImport: false,
      cleanupRemoveClientEntryAfterThreshold: false,
      cleanupPurgePayloadAfterThreshold: false,
      subtitleFetchAutomaticEnabled: false,
      leftoverDownloadCleanupEnabled: false
    });
    expect(buildLibraryAutomationFields(ALL_NO)).toEqual({
      autoSearchEnabled: false,
      missingSearchEnabled: false,
      upgradeSearchEnabled: false,
      removeEmptySourceFolders: false
    });
    expect(buildIndexerAutomationFields(ALL_NO)).toEqual({
      rssEnabled: false,
      automaticSearchEnabled: false
    });
  });

  it("maps a 'Yes' answer to its own setting turning on, and only that setting", () => {
    const onlyRenameYes: AutomationChoices = { ...ALL_NO, renameOnImport: true };
    expect(buildAutomationSettingsPayload(onlyRenameYes)).toMatchObject({
      renameOnImport: true,
      cleanupRemoveClientEntryAfterThreshold: false,
      cleanupPurgePayloadAfterThreshold: false,
      subtitleFetchAutomaticEnabled: false
    });

    const onlySubtitleFetchingYes: AutomationChoices = { ...ALL_NO, subtitleFetching: true };
    expect(buildAutomationSettingsPayload(onlySubtitleFetchingYes)).toMatchObject({
      renameOnImport: false,
      subtitleFetchAutomaticEnabled: true
    });

    const onlyMissingSearchYes: AutomationChoices = { ...ALL_NO, searchMissingTitles: true };
    expect(buildLibraryAutomationFields(onlyMissingSearchYes)).toEqual({
      autoSearchEnabled: true,
      missingSearchEnabled: true,
      upgradeSearchEnabled: false,
      removeEmptySourceFolders: false
    });

    const onlyLeftoverCleanupYes: AutomationChoices = { ...ALL_NO, leftoverCleanup: true };
    expect(buildLibraryAutomationFields(onlyLeftoverCleanupYes)).toEqual({
      autoSearchEnabled: false,
      missingSearchEnabled: false,
      upgradeSearchEnabled: false,
      removeEmptySourceFolders: true
    });

    const onlyRssYes: AutomationChoices = { ...ALL_NO, rssGrabs: true };
    expect(buildIndexerAutomationFields(onlyRssYes)).toEqual({
      rssEnabled: true,
      automaticSearchEnabled: false
    });

    const onlyLeftoverDownloadCleanupYes: AutomationChoices = { ...ALL_NO, leftoverDownloadCleanup: true };
    expect(buildAutomationSettingsPayload(onlyLeftoverDownloadCleanupYes)).toMatchObject({
      renameOnImport: false,
      subtitleFetchAutomaticEnabled: false,
      leftoverDownloadCleanupEnabled: true
    });
  });

  it("#932/#936: the leftover-download cleanup question defaults off and is not one of the per-library or per-indexer fields", () => {
    expect(buildAutomationSettingsPayload(ALL_NO).leftoverDownloadCleanupEnabled).toBe(false);
    expect(buildLibraryAutomationFields(ALL_YES)).not.toHaveProperty("leftoverDownloadCleanupEnabled");
    expect(buildIndexerAutomationFields(ALL_YES)).not.toHaveProperty("leftoverDownloadCleanupEnabled");
  });

  it("never writes the dead global CleanupEmptyFolders flag that #736 already removed from the UI", () => {
    expect(buildAutomationSettingsPayload(ALL_YES)).not.toHaveProperty("cleanupEmptyFolders");
  });

  it("turns the background scheduler on if any one of missing search, RSS or upgrades is on", () => {
    expect(buildAutomationSettingsPayload({ ...ALL_NO, searchMissingTitles: true }).autoStartJobs).toBe(true);
    expect(buildAutomationSettingsPayload({ ...ALL_NO, rssGrabs: true }).autoStartJobs).toBe(true);
    expect(buildAutomationSettingsPayload({ ...ALL_NO, qualityUpgrades: true }).autoStartJobs).toBe(true);
    expect(buildAutomationSettingsPayload(ALL_NO).autoStartJobs).toBe(false);
  });
});

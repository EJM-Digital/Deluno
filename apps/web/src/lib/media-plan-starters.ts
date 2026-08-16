export interface MediaPlanStarter {
  id: string;
  title: string;
  description: string;
  values: {
    name: string;
    mediaType: "movies" | "tv";
    searchIntervalOverrideHours: string;
    retryDelayOverrideHours: string;
    upgradeUntilCutoff: boolean;
    notes: string;
  };
}

export const MEDIA_PLAN_STARTERS: MediaPlanStarter[] = [
  {
    id: "everyday-movies",
    title: "Default: Everyday movies",
    description: "An editable default template for a normal movie library.",
    values: {
      name: "Default: Movies 1080p",
      mediaType: "movies",
      searchIntervalOverrideHours: "12",
      retryDelayOverrideHours: "6",
      upgradeUntilCutoff: true,
      notes: "Editable default plan: a dependable 1080p movie experience for a simple single-library setup."
    }
  },
  {
    id: "premium-4k",
    title: "Default: Premium 4K",
    description: "An editable quality-first default for a home-theatre movie collection.",
    values: {
      name: "Default: Premium 4K Movies",
      mediaType: "movies",
      searchIntervalOverrideHours: "12",
      retryDelayOverrideHours: "6",
      upgradeUntilCutoff: true,
      notes: "Editable default plan: a 4K and HDR-focused movie plan. Choose the matching quality goal and release preferences below."
    }
  },
  {
    id: "everyday-tv",
    title: "Default: Everyday TV",
    description: "An editable default template for a normal TV library.",
    values: {
      name: "Default: TV 1080p",
      mediaType: "tv",
      searchIntervalOverrideHours: "6",
      retryDelayOverrideHours: "3",
      upgradeUntilCutoff: true,
      notes: "Editable default plan: an everyday TV plan with steady missing-episode and upgrade searches."
    }
  },
  {
    id: "anime",
    title: "Default: Anime",
    description: "An editable default for anime-specific language, group, and format preferences.",
    values: {
      name: "Default: Anime",
      mediaType: "tv",
      searchIntervalOverrideHours: "6",
      retryDelayOverrideHours: "3",
      upgradeUntilCutoff: true,
      notes: "Editable default plan: choose anime release preferences below, then fine-tune language and quality for this library."
    }
  }
];

export function describeMediaPlanStarter(starter: MediaPlanStarter) {
  const mediaType = starter.values.mediaType === "tv" ? "TV" : "movies";
  const searchSchedule = starter.values.searchIntervalOverrideHours
    ? `search every ${starter.values.searchIntervalOverrideHours} hours`
    : "use the library search schedule";
  const retryDelay = starter.values.retryDelayOverrideHours
    ? `retry after ${starter.values.retryDelayOverrideHours} hours`
    : "use the library retry delay";
  const upgrades = starter.values.upgradeUntilCutoff ? "upgrade until the quality goal is met" : "keep the first accepted release";

  return `${mediaType}; ${searchSchedule}; ${retryDelay}; ${upgrades}.`;
}

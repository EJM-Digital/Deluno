/** The longest the server lets an import list wait between checks. Kept equal to IntakeSyncLimits.MaxHours in the backend (a test reads this file and checks). */
export const MAX_INTAKE_SYNC_INTERVAL_HOURS = 168;

/** The "Check the list" presets; every one is at or under the server's limit, so every one saves. */
export const INTAKE_SYNC_PRESETS = [
  { label: "Every 6 hours", value: "6" },
  { label: "Every 12 hours", value: "12" },
  { label: "Daily", value: "24" },
  { label: "Every 3 days", value: "72" },
  { label: "Weekly", value: "168" }
];

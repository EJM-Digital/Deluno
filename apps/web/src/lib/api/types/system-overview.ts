/**
 * GET /api/system/overview and the pushed SystemOverviewChanged event (same shape), #1098. Every value is a real
 * reading on the machine Deluno runs on; a value that cannot be read is null (or the whole section is missing) and
 * the System tab leaves it out rather than showing a made-up one.
 */
export interface SystemOverviewInstance {
  version: string;
  latest: boolean;
  channel: string;
  startedUtc: string;
  address: string;
  runsAs: "service" | "app" | "docker";
  cpuPercent: number;
  memoryBytes: number;
  workersBusy: number;
  oldestWaitingSeconds: number | null;
  answerMs: number;
  answerSlowestMs: number;
  jobsToday: number;
  jobsFailedToday: number;
  requestsToday: number;
  requestErrorsToday: number;
  dataBytes: number;
  databaseCount: number;
  lastBackupUtc: string | null;
  lastBackupBytes: number | null;
  restartsThisWeek: number;
  browsersLive: number;
}

export interface SystemOverviewMachine {
  os: string;
  uptimeSeconds: number;
  rebootPending: boolean | null;
  cpuTemperatureC: number | null;
  cpuPercent: number;
  cores: number;
  memoryUsedBytes: number;
  memoryTotalBytes: number;
  diskReadBytesPerSec: number;
  diskWriteBytesPerSec: number;
  diskIops: number;
}

export interface SystemOverviewNetwork {
  adapter: string | null;
  linkMbps: number | null;
  ip: string | null;
  inBytesPerSec: number;
  outBytesPerSec: number;
  internet: boolean;
  dnsLookupMs: number | null;
  nasMs: number | null;
  nasName: string | null;
  metadataMs: number | null;
  clockInSync: boolean | null;
}

export interface SystemOverviewDrive {
  name: string;
  path: string;
  totalBytes: number;
  freeBytes: number;
  delunoBytes: number;
  fullInDays: number | null;
  readBytesPerSec: number;
  writeBytesPerSec: number;
  iops: number;
  busyPercent: number;
  health: "good" | "warning" | "failing" | "unknown";
  healthNote: string | null;
}

export interface SystemOverview {
  instance: SystemOverviewInstance;
  machine: SystemOverviewMachine;
  network: SystemOverviewNetwork;
  drives: SystemOverviewDrive[];
  /** The last 10 minutes, one reading per 10 seconds. */
  history: { cpu: number[]; memory: number[]; disk: number[]; netIn: number[] };
}

/** Pushed on the "dashboard" subject every 2 s while someone is watching: the same shape as the GET. */
export type SystemOverviewChangedEvent = SystemOverview;

/** One connection health-checked (regularly, or by a person pressing Test), pushed as it is answered. */
export interface ConnectionCheckedEvent {
  kind: string;
  id: string;
  name: string;
  ok: boolean;
  slow: boolean;
  message: string | null;
  checkedUtc: string;
}

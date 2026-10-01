/** GET /api/system/log, the pushed SystemLogEntry event (same entry shape), and GET /api/system/databases (#1098). */
export interface SystemLogEntry {
  id: string;
  timeUtc: string;
  level: "error" | "warning" | "info";
  message: string;
  source: string | null;
}

export interface SystemLogResponse {
  entries: SystemLogEntry[];
  errorsToday: number;
  warningsToday: number;
  latestLogFile: string | null;
}

export interface SystemDatabaseItem {
  key: string;
  name: string;
  sizeBytes: number;
}

export interface SystemDatabasesResponse {
  databases: SystemDatabaseItem[];
  totalBytes: number;
  integrityCheckedUtc: string | null;
  integrityOk: boolean | null;
  integrityNote: string | null;
  compactedUtc: string | null;
}

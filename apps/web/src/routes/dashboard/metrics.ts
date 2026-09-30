/** `/api/dashboard/metrics`: counts of stored rows grouped by day. Only the parts the Dashboard reads. */
export interface DashboardMetricPoint {
  date: string;
  value: number;
}

export interface DashboardMetrics {
  days: number;
  librarySize: DashboardMetricPoint[];
  titlesAdded: DashboardMetricPoint[];
}

export const emptyDashboardMetrics: DashboardMetrics = { days: 30, librarySize: [], titlesAdded: [] };

import type { JSX } from "react";
import type { DashboardSummary } from "../../lib/api";
import { ListCard } from "../../components/ui/list-card";
import { Plain } from "./parts";

/** Stub: the real Downloads tab replaces this file (#1082). */
export function DownloadsTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  void summary;
  return (
    <ListCard title="Downloads">
      <Plain>This tab is being built.</Plain>
    </ListCard>
  );
}

/** How many problems this tab has, for its badge. */
export function downloadsTabAttention(summary: DashboardSummary | undefined): number {
  void summary;
  return 0;
}

import type { JSX } from "react";
import type { DashboardSummary } from "../../lib/api";
import { ListCard } from "../../components/ui/list-card";
import { Plain } from "./parts";

/** Stub: the real System tab replaces this file (#1082). */
export function SystemTab({ summary }: { summary: DashboardSummary | undefined }): JSX.Element {
  void summary;
  return (
    <ListCard title="System">
      <Plain>This tab is being built.</Plain>
    </ListCard>
  );
}

/** How many problems this tab has, for its badge. */
export function systemTabAttention(summary: DashboardSummary | undefined): number {
  void summary;
  return 0;
}

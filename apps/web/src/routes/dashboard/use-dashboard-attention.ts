import { useMemo } from "react";
import { useRouteSources } from "../../lib/route-cache";
import type { DashboardSummary } from "../../lib/api";
import { attentionSources, computeAttention, type DashboardAttention, type SetupNeed } from "./attention";

/**
 * The Dashboard's problems, counted once (see `attention.ts`). Reads the same
 * stored lists the sidebar reads, every one of them whatever tab is open, so
 * the count never depends on which screens were visited first (#1082).
 */
export function useDashboardAttention(summary: DashboardSummary | undefined, setup: SetupNeed[] = []): DashboardAttention {
  const { data } = useRouteSources(attentionSources);
  return useMemo(
    () => computeAttention(summary, data, setup),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [summary, data, setup]
  );
}

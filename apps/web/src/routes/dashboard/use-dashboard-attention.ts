import { useMemo } from "react";
import { queryClient, useRouteSources } from "../../lib/route-cache";
import { sharedSources } from "../../lib/shared-sources";
import type { OrphanedDownloadItem } from "../../lib/api";
import type { DashboardSummary } from "../../lib/api";
import { attentionSources, computeAttention, type DashboardAttention, type SetupNeed } from "./attention";

/**
 * The Dashboard's problems, counted once (see `attention.ts`). Reads the same
 * stored lists the sidebar keeps warm, so a revisit is answered from the cache.
 */
export function useDashboardAttention(summary: DashboardSummary | undefined, setup: SetupNeed[] = []): DashboardAttention {
  const { data } = useRouteSources(attentionSources);
  // Leftover folders are found by walking folders on the server: only read when something already has.
  const orphans = queryClient.getQueryData<OrphanedDownloadItem[]>(sharedSources.orphans.key);
  return useMemo(
    () => computeAttention(summary, { ...data, orphans }, setup),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [summary, data, orphans, setup]
  );
}

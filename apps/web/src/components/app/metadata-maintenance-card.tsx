import { useEffect, useState } from "react";
import { Loader2, RefreshCw, SearchCheck } from "lucide-react";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../ui/list-card";
import {
  fetchJson,
  type MetadataProviderStatus,
  type MetadataRefreshJobsResponse,
  type MetadataTestResponse
} from "../../lib/api";
import { describeMetadataCheck } from "../../lib/metadata-check";
import { plural } from "../../lib/plural";

const REFRESH_JOBS = [
  {
    key: "missing",
    name: "Fill in missing details",
    sub: "Movies and TV",
    // Deliberately does *not* re-fetch artwork a title already has, even when
    // Deluno now caches a larger size. "Missing" means missing. A title whose
    // poster is merely older is handled by the two "Refresh all" passes below,
    // which is said out loud there rather than left to be discovered.
    description: "Only checks titles that are missing artwork, a description or a rating. Artwork a title already has is left alone.",
    mediaType: "all" as const,
    forceAll: false
  },
  {
    key: "movies",
    name: "Refresh all Movies",
    sub: "Movies",
    description: "Fetches the latest details for the whole movie library, and re-downloads artwork at the size Deluno caches now. Use after changing language or region, or to bring older titles up to the current artwork quality.",
    mediaType: "movies" as const,
    forceAll: true
  },
  {
    key: "tv",
    name: "Refresh all TV shows",
    sub: "TV shows",
    description: "Fetches the latest details for the whole TV library, and re-downloads artwork at the size Deluno caches now. Use after changing language or region, or to bring older titles up to the current artwork quality.",
    mediaType: "tv" as const,
    forceAll: true
  }
] as const;

export function MetadataMaintenanceCard({ onRefresh }: { onRefresh?: () => void }) {
  const [metadataStatus, setMetadataStatus] = useState<MetadataProviderStatus | null>(null);
  const [testResult, setTestResult] = useState<MetadataTestResponse | null>(null);
  const [jobResult, setJobResult] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    void fetchJson<MetadataProviderStatus>("/api/metadata/status")
      .then((status) => {
        if (active) setMetadataStatus(status);
      })
      .catch(() => {
        if (active) setMetadataStatus(null);
      });
    return () => {
      active = false;
    };
  }, []);

  async function checkTitleMatching() {
    setBusy("test");
    setTestResult(null);
    try {
      setTestResult(
        await fetchJson<MetadataTestResponse>("/api/metadata/test", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ query: "The Matrix", mediaType: "movies", year: 1999 })
        })
      );
      // Check now also closes the metadata circuits on the server, so the chip
      // reports the service answering now, not a pause from an earlier blip.
      void fetchJson<MetadataProviderStatus>("/api/metadata/status")
        .then(setMetadataStatus)
        .catch(() => undefined);
    } catch (error) {
      setTestResult({
        isConfigured: false,
        resultCount: 0,
        message: error instanceof Error ? error.message : "The check could not be run."
      } as MetadataTestResponse);
    } finally {
      setBusy(null);
    }
  }

  async function queueRefresh(job: (typeof REFRESH_JOBS)[number]) {
    setBusy(`job:${job.key}`);
    try {
      const targets =
        job.mediaType === "all"
          ? ["/api/movies/metadata/jobs", "/api/series/metadata/jobs"]
          : [job.mediaType === "movies" ? "/api/movies/metadata/jobs" : "/api/series/metadata/jobs"];
      const results = await Promise.all(
        targets.map((path) =>
          fetchJson<MetadataRefreshJobsResponse>(path, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ forceAll: job.forceAll, take: 500 })
          })
        )
      );

      // The API keeps this bounded and reports the remaining work, so the UI
      // never needs to load a whole catalogue just to describe the operation.
      const enqueued = results.reduce((total, item) => total + item.enqueuedCount, 0);
      const remaining = results.reduce((total, item) => total + item.remainingCount, 0);
      const summary =
        results.length === 1
          ? results[0].message
          : remaining > 0
            ? `Queued ${plural(enqueued, "title")}. Another ${remaining.toLocaleString()} still to go — Deluno keeps working through them in the background.`
            : enqueued
              ? `Queued ${plural(enqueued, "title")}. That is everything that needs refreshing.`
              : "Nothing needs refreshing.";
      setJobResult((current) => ({ ...current, [job.key]: summary }));
      onRefresh?.();
    } catch (error) {
      setJobResult((current) => ({ ...current, [job.key]: error instanceof Error ? error.message : "Could not queue" }));
    } finally {
      setBusy(null);
    }
  }

  const check = describeMetadataCheck(metadataStatus, testResult);

  return (
    <ListCard
      title="Metadata maintenance"
      count="System-wide checks and updates for Movies and TV shows"
      actions={
        <Button type="button" variant="outline" size="sm" onClick={() => void checkTitleMatching()} disabled={busy !== null}>
          {busy === "test" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <SearchCheck className="h-3.5 w-3.5" />}
          Check now
        </Button>
      }
    >
      <ListTable columns={[{ label: "Service" }, { label: "Last check", width: "minmax(0,1.6fr)" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]} chevron={false}>
        <ListRow>
          <ListNameCell name="Title matching and library details" sub="Posters, descriptions, ratings, dates" />
          <ListCell primary={check.primary} secondary={check.secondary} />
          <ListCell mobile>
            <Chip tone={check.tone}>{check.chip}</Chip>
          </ListCell>
        </ListRow>
      </ListTable>

      <div className="border-t border-hairline">
        <div className="px-(--card-pad-x) py-3 text-(length:--type-caption) text-muted-foreground">
          These are system maintenance jobs. They run in the background and do not change your library files.
        </div>
        <ListTable
          listId="metadata-maintenance-jobs"
          items={REFRESH_JOBS}
          columns={[
            { id: "job", label: "Job", sort: (job) => job.name.toLowerCase() },
            { id: "what", label: "What it does", width: "minmax(0,1.8fr)", sort: (job) => job.description.toLowerCase() },
            { id: "run", label: "Run", width: "120px", mobile: true, srOnly: true }
          ]}
          chevron={false}
        >
          {(rows) => rows.map((job) => (
            <ListRow key={job.key}>
              <ListNameCell name={job.name} sub={job.sub} />
              <ListCell primary={job.description} secondary={jobResult[job.key]} />
              <ListCell mobile align="end">
                <Button type="button" variant="outline" size="sm" onClick={() => void queueRefresh(job)} disabled={busy !== null}>
                  {busy === `job:${job.key}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
                  Run
                </Button>
              </ListCell>
            </ListRow>
          ))}
        </ListTable>
      </div>
    </ListCard>
  );
}

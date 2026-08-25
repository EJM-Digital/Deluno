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

const REFRESH_JOBS = [
  {
    key: "missing",
    name: "Fill in missing details",
    sub: "Movies and TV",
    description: "Only checks titles that are missing artwork, a description or a rating.",
    mediaType: "all" as const,
    forceAll: false
  },
  {
    key: "movies",
    name: "Refresh all Movies",
    sub: "Movies",
    description: "Fetches the latest details for the whole movie library. Use after changing language or region.",
    mediaType: "movies" as const,
    forceAll: true
  },
  {
    key: "tv",
    name: "Refresh all TV shows",
    sub: "TV shows",
    description: "Fetches the latest details for the whole TV library. Use after changing language or region.",
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
            ? `Queued ${enqueued.toLocaleString()} ${enqueued === 1 ? "title" : "titles"}. Another ${remaining.toLocaleString()} still to go — Deluno keeps working through them in the background.`
            : enqueued
              ? `Queued ${enqueued.toLocaleString()} ${enqueued === 1 ? "title" : "titles"}. That is everything that needs refreshing.`
              : "Nothing needs refreshing.";
      setJobResult((current) => ({ ...current, [job.key]: summary }));
      onRefresh?.();
    } catch (error) {
      setJobResult((current) => ({ ...current, [job.key]: error instanceof Error ? error.message : "Could not queue" }));
    } finally {
      setBusy(null);
    }
  }

  const ready = Boolean(metadataStatus?.isConfigured);

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
          <ListCell
            primary={testResult ? (testResult.isConfigured ? `${testResult.resultCount} ${testResult.resultCount === 1 ? "match" : "matches"} for “The Matrix”` : "The check could not reach the service") : "Not checked this session"}
            secondary={testResult?.message ?? (ready ? "Deluno can match titles and collect their details." : "You can still add a movie or show by hand.")}
          />
          <ListCell mobile>
            <Chip tone={testResult ? (testResult.isConfigured ? "ok" : "bad") : ready ? "ok" : "warn"}>
              {testResult ? (testResult.isConfigured ? "Working" : "Failed") : ready ? "Ready" : "Unavailable"}
            </Chip>
          </ListCell>
        </ListRow>
      </ListTable>

      <div className="border-t border-hairline">
        <div className="px-[var(--card-pad-x)] py-3 text-[length:var(--type-caption)] text-muted-foreground">
          These are system maintenance jobs. They run in the background and do not change your library files.
        </div>
        <ListTable columns={[{ label: "Job" }, { label: "What it does", width: "minmax(0,1.8fr)" }, { label: "Run", width: "120px", mobile: true, srOnly: true }]} chevron={false}>
          {REFRESH_JOBS.map((job) => (
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

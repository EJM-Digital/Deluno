/**
 * Shared "Needs attention" (import recovery) page component.
 * Used by both /movies/import and /tv/import.
 * Shows failed imports, processor timeouts, and unresolved handoffs with
 * plain-English summaries for beginners and full details for advanced users.
 */
import { useState } from "react";
import { Link, useLoaderData, useRevalidator } from "react-router-dom";
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  RefreshCw,
  RotateCw,
  Trash2
} from "lucide-react";
import {
  fetchJson,
  type MovieImportRecoveryCase,
  type MovieImportRecoverySummary,
  type SeriesImportRecoveryCase,
  type SeriesImportRecoverySummary
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { cn } from "../lib/utils";
import { Button } from "../components/ui/button";
import { Badge } from "../components/ui/badge";
import { GlassTile } from "../components/shell/page-hero";
import { EmptyState } from "../components/shell/empty-state";
import { toast } from "../components/shell/toaster";
import { RouteSkeleton } from "../components/shell/skeleton";

/* ── Types ───────────────────────────────────────────────────────────── */

type AnyCase = (MovieImportRecoveryCase | SeriesImportRecoveryCase) & { _mediaType: "movie" | "series" };

interface MoviesImportLoaderData {
  recovery: MovieImportRecoverySummary;
}

interface TvImportLoaderData {
  recovery: SeriesImportRecoverySummary;
}

/* ── Loaders ─────────────────────────────────────────────────────────── */

export async function moviesImportLoader(): Promise<MoviesImportLoaderData> {
  const recovery = await fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery");
  return { recovery };
}

export async function tvImportLoader(): Promise<TvImportLoaderData> {
  const recovery = await fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery");
  return { recovery };
}

/* ── Page components ─────────────────────────────────────────────────── */

export function MoviesImportPage() {
  const data = useLoaderData() as MoviesImportLoaderData | undefined;
  if (!data) return <RouteSkeleton />;
  const cases: AnyCase[] = data.recovery.recentCases.map((c) => ({ ...c, _mediaType: "movie" as const }));
  return <ImportRecoveryView mediaLabel="Movies" openCount={data.recovery.openCount} cases={cases} />;
}

export function TvImportPage() {
  const data = useLoaderData() as TvImportLoaderData | undefined;
  if (!data) return <RouteSkeleton />;
  const cases: AnyCase[] = data.recovery.recentCases.map((c) => ({ ...c, _mediaType: "series" as const }));
  return <ImportRecoveryView mediaLabel="TV Shows" openCount={data.recovery.openCount} cases={cases} />;
}

/* ── Shared view ─────────────────────────────────────────────────────── */

function ImportRecoveryView({
  mediaLabel,
  openCount,
  cases
}: {
  mediaLabel: string;
  openCount: number;
  cases: AnyCase[];
}) {
  const revalidator = useRevalidator();
  const [busyId, setBusyId] = useState<string | null>(null);

  async function handleDismiss(c: AnyCase) {
    setBusyId(c.id);
    try {
      const path = c._mediaType === "movie" ? `/api/movies/import-recovery/${c.id}` : `/api/series/import-recovery/${c.id}`;
      const res = await authedFetch(path, { method: "DELETE" });
      if (!res.ok) throw new Error("Could not dismiss this issue.");
      toast.success("Issue dismissed");
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Dismiss failed.");
    } finally {
      setBusyId(null);
    }
  }

  async function handleRetry(c: AnyCase) {
    setBusyId(`retry-${c.id}`);
    try {
      const retryPayload = tryParseRetryRequest(c.detailsJson);
      if (!retryPayload) {
        throw new Error("No retry details stored. Queue a fresh import from the Queue page.");
      }
      await fetchJson("/api/filesystem/import/jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(retryPayload)
      });
      toast.success("Retry queued — check the Queue page for progress.");
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Retry failed.");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      {/* Beginner-friendly explanation */}
      <GlassTile>
        <div className="flex flex-col gap-3 px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)] sm:flex-row sm:items-start sm:justify-between">
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <h2 className="font-display text-[15px] font-semibold text-foreground">
                {mediaLabel} — Needs attention
              </h2>
              {openCount > 0 ? (
                <Badge variant="destructive" className="text-[10px]">{openCount} open</Badge>
              ) : (
                <Badge variant="success" className="text-[10px]">All clear</Badge>
              )}
            </div>
            <p className="mt-1 max-w-[60ch] text-[12px] leading-relaxed text-muted-foreground">
              These are downloads that completed in your client but couldn't be imported automatically.
              Each issue includes a plain-English explanation and a recommended next step.
              Dismiss an issue once you've resolved it, or use Retry to attempt import again.
            </p>
          </div>
          <Button size="sm" variant="ghost" className="shrink-0 gap-2" onClick={() => revalidator.revalidate()}>
            <RefreshCw className="h-3.5 w-3.5" />
            Refresh
          </Button>
        </div>
      </GlassTile>

      {/* Case list */}
      <GlassTile>
        {cases.length === 0 ? (
          <div className="p-[var(--tile-pad)]">
            <div className="flex items-center gap-3 rounded-xl border border-success/20 bg-success/5 p-4">
              <CheckCircle2 className="h-5 w-5 shrink-0 text-success" />
              <div>
                <p className="font-semibold text-foreground">Everything imported successfully</p>
                <p className="mt-0.5 text-[12px] text-muted-foreground">
                  When an import can't complete automatically, the issue will appear here with a
                  clear explanation and recommended action.
                </p>
              </div>
            </div>
          </div>
        ) : (
          <div className="divide-y divide-hairline">
            {cases.map((c) => (
              <RecoveryCard
                key={c.id}
                item={c}
                busy={busyId}
                onDismiss={handleDismiss}
                onRetry={handleRetry}
              />
            ))}
          </div>
        )}
      </GlassTile>

      {/* Help callout */}
      <div className="rounded-2xl border border-hairline bg-muted/20 px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.7)]">
        <p className="text-[12px] font-semibold text-foreground">Need help resolving an issue?</p>
        <p className="mt-1 text-[12px] leading-relaxed text-muted-foreground">
          Most import failures are caused by missing source files, destination permission errors, or
          processor timeouts. Check the{" "}
          <Link to="/queue" className="text-primary hover:underline">Queue page</Link>{" "}
          for more detail, or open a{" "}
          <Link to="/system/docs" className="text-primary hover:underline">workflow guide</Link>{" "}
          for step-by-step troubleshooting.
        </p>
      </div>
    </div>
  );
}

/* ── Recovery card ───────────────────────────────────────────────────── */

function RecoveryCard({
  item,
  busy,
  onDismiss,
  onRetry
}: {
  item: AnyCase;
  busy: string | null;
  onDismiss: (c: AnyCase) => Promise<void>;
  onRetry: (c: AnyCase) => Promise<void>;
}) {
  const [expanded, setExpanded] = useState(false);
  const isBusy = busy !== null;
  const failureLabel = failureKindLabel(item.failureKind);

  return (
    <div className="px-[var(--tile-pad)] py-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start">
        <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl border border-warning/25 bg-warning/10">
          <AlertTriangle className="h-4 w-4 text-warning" />
        </div>

        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-semibold text-foreground">{item.title}</span>
            <Badge variant="warning" className="text-[9.5px]">{failureLabel}</Badge>
            <span className="text-[11px] text-muted-foreground">{formatDate(item.detectedUtc)}</span>
          </div>

          {/* Plain-English summary — visible to everyone */}
          <p className="mt-1.5 text-[13px] leading-relaxed text-foreground">{item.summary}</p>

          {/* Recommended action — prominent for beginners */}
          <div className="mt-2 rounded-lg border border-primary/20 bg-primary/5 px-3 py-2">
            <p className="text-[11px] font-semibold uppercase tracking-wide text-primary">Recommended action</p>
            <p className="mt-0.5 text-[12px] text-foreground">{item.recommendedAction}</p>
          </div>

          {/* Advanced: raw details — collapsed by default */}
          {item.detailsJson && (
            <div className="mt-2">
              <button
                type="button"
                className="inline-flex items-center gap-1 text-[11px] font-semibold text-muted-foreground hover:text-foreground"
                onClick={() => setExpanded((v) => !v)}
                aria-expanded={expanded}
              >
                {expanded ? <ChevronUp className="h-3 w-3" /> : <ChevronDown className="h-3 w-3" />}
                {expanded ? "Hide technical details" : "Show technical details"}
              </button>
              {expanded && (
                <pre className="mt-2 max-h-48 overflow-auto rounded-lg border border-hairline bg-surface-2 p-3 font-mono text-[10.5px] text-muted-foreground">
                  {tryPrettyJson(item.detailsJson)}
                </pre>
              )}
            </div>
          )}
        </div>

        {/* Actions */}
        <div className="flex shrink-0 flex-wrap gap-2 sm:flex-col">
          <Button
            size="sm"
            variant="outline"
            className="gap-1.5"
            disabled={isBusy}
            onClick={() => void onRetry(item)}
            id={`recovery-retry-${item.id}`}
          >
            <RotateCw className="h-3.5 w-3.5" />
            Retry
          </Button>
          <Button
            size="sm"
            variant="ghost"
            className="gap-1.5 text-muted-foreground hover:text-destructive"
            disabled={isBusy}
            onClick={() => void onDismiss(item)}
            id={`recovery-dismiss-${item.id}`}
          >
            <Trash2 className="h-3.5 w-3.5" />
            Dismiss
          </Button>
        </div>
      </div>
    </div>
  );
}

/* ── Helpers ─────────────────────────────────────────────────────────── */

function failureKindLabel(kind: string): string {
  const labels: Record<string, string> = {
    "processor-timeout": "Processor timed out",
    "missing-source": "Source file missing",
    "permission-denied": "Permission error",
    "destination-conflict": "Destination conflict",
    "quality-block": "Blocked by quality policy",
    "unmatched": "Could not match to library",
    "corrupt": "Corrupt or unreadable file",
    "download-failed": "Download failed in client",
    "import-failed": "Import pipeline failed"
  };
  return labels[kind] ?? kind;
}

function formatDate(utc: string): string {
  return new Date(utc).toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  });
}

function tryPrettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

function tryParseRetryRequest(detailsJson: string | null): unknown | null {
  if (!detailsJson) return null;
  try {
    const parsed = JSON.parse(detailsJson) as Record<string, unknown>;
    // The worker stores the original ImportExecuteRequest payload in detailsJson
    if (parsed.preview) return parsed;
    return null;
  } catch {
    return null;
  }
}

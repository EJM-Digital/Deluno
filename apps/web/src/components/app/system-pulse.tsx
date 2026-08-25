/**
 * System pulse — "is Deluno healthy right now?", answered before anything else
 * on the Dashboard (#270).
 *
 * Every number comes from `/api/monitoring/dashboard`, which Deluno already
 * computed for the panel buried in System. Nothing here is derived, averaged or
 * estimated in the browser; the cells only format, colour and animate what the
 * snapshot says. Each cell links to the page where that number is dealt with,
 * so a red light is one click from its cause.
 *
 * Presented as an instrument panel rather than a table because that is how it
 * is read: at a glance, from a distance, looking for the one thing that is not
 * green. Colour is never the only signal — every light sits beside its own
 * label and value — and all motion stops under `prefers-reduced-motion`.
 *
 * Contract: GET /api/monitoring/dashboard → MonitoringDashboardSnapshot.
 */
import { Link } from "react-router-dom";
import { CountUp } from "../ui/count-up";
import { RadialGauge } from "../ui/radial-gauge";
import { StatusLed, type LedTone } from "../ui/status-led";
import { cn } from "../../lib/utils";
import type { MonitoringDashboardSnapshot } from "../../lib/api";

/** Below this much free space, storage stops being background information. */
const STORAGE_WARN_PERCENT = 15;
const STORAGE_DANGER_PERCENT = 5;

/** A local API answering slower than this is worth noticing. */
const LATENCY_WARN_MS = 750;

interface PulseCell {
  label: string;
  value: string;
  numeric?: number;
  help: string;
  tone: LedTone;
  pulse?: boolean;
  href: string;
}

export function SystemPulse({ snapshot, className }: { snapshot: MonitoringDashboardSnapshot | null; className?: string }) {
  if (!snapshot) {
    return (
      <section className={cn("flex min-h-[var(--list-header-height)] items-center gap-3 rounded-2xl border border-hairline bg-card px-[var(--card-pad-x)] shadow-card dark:border-white/[0.07]", className)}>
        <StatusLed tone="warn" />
        <span className="text-[length:var(--type-body-sm)] font-medium text-foreground">System not reporting</span>
        <span className="text-[length:var(--type-caption)] text-muted-foreground">monitoring is unavailable</span>
      </section>
    );
  }

  const { readiness, storage, performance } = snapshot;
  const latency = performance.apiLatency;
  const cells = buildCells(snapshot);
  const worst = worstTone(cells.map((cell) => cell.tone).concat(readiness.ready ? "ok" : "danger"));

  return (
    <section
      aria-label="System status"
      className={cn(
        "relative overflow-hidden rounded-2xl border bg-card shadow-card",
        className,
        worst === "danger"
          ? "border-destructive/30 dark:border-destructive/25"
          : worst === "warn"
            ? "border-warning/30 dark:border-warning/25"
            : "border-hairline dark:border-white/[0.07]"
      )}
    >
      {/* Ambient wash keyed to the worst thing on the panel: a healthy board is
          calm, a broken one is visibly warm before you read a word of it. */}
      <span
        aria-hidden
        className={cn(
          "pointer-events-none absolute -right-24 -top-28 h-64 w-64 rounded-full blur-[90px]",
          worst === "danger" ? "bg-destructive/20" : worst === "warn" ? "bg-warning/20" : "bg-success/10"
        )}
      />

      <div className="relative grid h-full gap-[var(--grid-gap)] p-[var(--card-pad-x)] lg:grid-cols-[auto_minmax(0,1fr)] lg:items-stretch">
        <StorageDial storage={storage} />

        <div className="grid min-w-0 grid-cols-2 gap-px overflow-hidden rounded-xl bg-hairline/70 sm:grid-cols-3 xl:grid-cols-5 dark:bg-white/[0.06]">
          <PulseTile
            cell={{
              label: "System",
              value: readiness.ready ? "Ready" : "Not ready",
              help: readiness.failedChecks > 0
                ? `${readiness.failedChecks} of ${readiness.totalChecks} checks failing`
                : `${readiness.totalChecks} checks passing`,
              tone: readiness.ready ? "ok" : "danger",
              pulse: readiness.ready,
              href: "/system"
            }}
          />
          {cells.map((cell) => (
            <PulseTile key={cell.label} cell={cell} />
          ))}
          <PulseTile
            cell={{
              label: "API response",
              value: latency.requestCount === 0 ? "Idle" : `${Math.round(latency.p95Ms)} ms`,
              help: latency.requestCount === 0
                ? "no requests in the window"
                : latency.errorCount > 0
                  ? `${latency.errorRatePercent.toFixed(1)}% erroring`
                  : `p95 of ${latency.requestCount.toLocaleString()}`,
              tone: latency.errorCount > 0 || latency.p95Ms > LATENCY_WARN_MS ? "warn" : latency.requestCount === 0 ? "idle" : "ok",
              href: "/system"
            }}
          />
        </div>
      </div>
    </section>
  );
}

function PulseTile({ cell }: { cell: PulseCell }) {
  return (
    <Link
      to={cell.href}
      className="group flex min-w-0 flex-col justify-center gap-1 bg-card px-3 py-2.5 transition-colors hover:bg-primary/[0.06] focus-visible:relative focus-visible:z-10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
    >
      <span className="flex min-w-0 items-center gap-1.5">
        <StatusLed tone={cell.tone} pulse={cell.pulse} />
        <span className="truncate text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
          {cell.label}
        </span>
      </span>
      <span
        className={cn(
          "block truncate text-[length:var(--type-title-sm)] font-semibold tabular-nums leading-none",
          cell.tone === "danger" ? "text-destructive" : cell.tone === "warn" ? "text-warning" : "text-foreground"
        )}
      >
        {cell.numeric === undefined ? cell.value : <CountUp value={cell.numeric} />}
      </span>
      <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{cell.help}</span>
    </Link>
  );
}

function StorageDial({ storage }: { storage: MonitoringDashboardSnapshot["storage"] }) {
  // A drive Deluno cannot measure is not a drive that is full. Saying so beats
  // drawing an empty dial over "0 B".
  if (storage.freeBytes === null || storage.freeBytes === undefined || !storage.totalBytes) {
    return (
      <Link
        to="/settings/media-management"
        className="flex h-[104px] w-[104px] shrink-0 flex-col items-center justify-center gap-1 rounded-xl border border-dashed border-hairline text-center transition-colors hover:border-primary/40"
      >
        <span className="text-[length:var(--type-body-sm)] font-semibold text-foreground">Storage</span>
        <span className="px-2 text-[length:var(--type-micro)] text-muted-foreground">free space could not be read</span>
      </Link>
    );
  }

  const percent = storage.freePercent ?? (storage.freeBytes / storage.totalBytes) * 100;
  const used = 1 - storage.freeBytes / storage.totalBytes;
  const tone = storage.lowStorage || percent <= STORAGE_DANGER_PERCENT
    ? "danger"
    : percent <= STORAGE_WARN_PERCENT
      ? "warning"
      : "primary";

  return (
    <Link
      to="/settings/media-management"
      aria-label={`Storage: ${formatBytes(storage.freeBytes)} free of ${formatBytes(storage.totalBytes)}`}
      className="group flex shrink-0 items-center gap-3 self-center rounded-xl px-1 transition-transform hover:scale-[1.02] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      <RadialGauge
        className="h-[104px] w-[104px]"
        value={used}
        tone={tone}
        label={formatBytes(storage.freeBytes)}
        caption="free"
      />
      <span className="hidden min-w-0 flex-col lg:flex">
        <span className="text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
          Storage
        </span>
        <span className="mt-1 text-[length:var(--type-body-sm)] font-medium text-foreground">
          {Math.round(used * 100)}% used
        </span>
        <span className="text-[length:var(--type-caption)] text-muted-foreground">
          of {formatBytes(storage.totalBytes)}
        </span>
      </span>
    </Link>
  );
}

function buildCells(snapshot: MonitoringDashboardSnapshot): PulseCell[] {
  const { services } = snapshot;
  const working = services.activeJobs + services.queuedJobs;

  return [
    {
      label: "Sources",
      value: services.indexersTotal === 0 ? "None" : `${services.indexersHealthy}/${services.indexersTotal}`,
      help: services.indexersTotal === 0
        ? "none set up yet"
        : services.indexersHealthy === services.indexersTotal
          ? "all responding"
          : `${services.indexersTotal - services.indexersHealthy} not responding`,
      tone: services.indexersTotal === 0
        ? "idle"
        : services.indexersHealthy < services.indexersTotal
          ? "warn"
          : "ok",
      href: "/indexers"
    },
    {
      label: "Clients",
      value: services.downloadClientsTotal === 0 ? "None" : `${services.downloadClientsHealthy}/${services.downloadClientsTotal}`,
      help: services.downloadClientsTotal === 0
        ? "none set up yet"
        : services.downloadClientsHealthy === services.downloadClientsTotal
          ? "all reachable"
          : `${services.downloadClientsTotal - services.downloadClientsHealthy} unreachable`,
      tone: services.downloadClientsTotal === 0
        ? "idle"
        : services.downloadClientsHealthy < services.downloadClientsTotal
          ? "warn"
          : "ok",
      href: "/indexers/download-clients"
    },
    {
      label: "Work queue",
      value: String(services.failedJobs > 0 ? services.failedJobs : working),
      numeric: services.failedJobs > 0 ? services.failedJobs : working,
      help: services.failedJobs > 0
        ? `${services.failedJobs === 1 ? "job" : "jobs"} failed · ${working} queued`
        : working === 0
          ? "nothing queued"
          : `${services.activeJobs} running · ${services.queuedJobs} waiting`,
      tone: services.failedJobs > 0 ? "warn" : working > 0 ? "info" : "idle",
      // A queue with work in it is the one light that should look busy.
      pulse: services.failedJobs === 0 && services.activeJobs > 0,
      href: "/activity"
    }
  ];
}

const TONE_RANK: Record<LedTone, number> = { danger: 3, warn: 2, info: 1, ok: 0, idle: 0 };

function worstTone(tones: LedTone[]): LedTone {
  return tones.reduce<LedTone>((worst, tone) => (TONE_RANK[tone] > TONE_RANK[worst] ? tone : worst), "ok");
}

const UNITS = ["B", "KB", "MB", "GB", "TB", "PB"] as const;

/** Decimal units, because that is what a drive's label says. */
function formatBytes(bytes: number) {
  if (bytes <= 0) return "0 B";
  const index = Math.min(UNITS.length - 1, Math.floor(Math.log10(bytes) / 3));
  const value = bytes / 1000 ** index;
  return `${value.toFixed(value >= 100 || index === 0 ? 0 : 1)} ${UNITS[index]}`;
}

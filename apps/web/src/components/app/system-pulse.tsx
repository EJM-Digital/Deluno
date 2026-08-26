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
import type { MachineTelemetrySample, MonitoringDashboardSnapshot } from "../../lib/api";

/** Below this much free space, storage stops being background information. */
const STORAGE_WARN_PERCENT = 15;
const STORAGE_DANGER_PERCENT = 5;

/** A local API answering slower than this is worth noticing. */
const LATENCY_WARN_MS = 750;

/** Above this, a machine reading stops being background information (#272). */
const MACHINE_WARN_PERCENT = 85;

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

  const { readiness, storage, performance, machine } = snapshot;
  const latency = performance.apiLatency;
  const cells = buildCells(snapshot);
  const worst = worstTone(cells.map((cell) => cell.tone).concat(readiness.ready ? "ok" : "danger"));

  return (
    <section
      aria-label="System status"
      className={cn(
        "relative flex flex-col overflow-hidden rounded-2xl border bg-card shadow-card",
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

      <div className="relative grid flex-1 gap-[var(--grid-gap)] p-[var(--card-pad-x)] lg:grid-cols-[auto_minmax(0,1fr)] lg:items-stretch">
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
                  // "p95 of 1,027" read as "p95 equals 1,027" — two unrelated
                  // numbers welded together, one of them jargon. The headline
                  // is a 95th percentile, so say what that means about the
                  // requests it was measured over.
                  : `95% of ${latency.requestCount.toLocaleString()} were faster`,
              tone: latency.errorCount > 0 || latency.p95Ms > LATENCY_WARN_MS ? "warn" : latency.requestCount === 0 ? "idle" : "ok",
              href: "/system"
            }}
          />
        </div>
      </div>

      {machine ? <MachineStrip machine={machine} /> : null}
    </section>
  );
}

/**
 * How hard the machine is working (#272).
 *
 * Deluno could say how full a drive was and nothing about how busy it was, so
 * when an import crawled it could not say whether the cause was Deluno, the
 * disk, or something else on the box — a question the arr suite also fails to
 * answer.
 *
 * A strip rather than three more tiles, deliberately. These are numbers people
 * go looking for when something is slow, not numbers they scan every visit, and
 * this pane is held to fitting one screen: a tile row would have cost real
 * height for readings that are usually unremarkable.
 *
 * Both disk figures are here because having only one cannot tell "Deluno is
 * hammering the disk" from "something else is", which is the whole question.
 */
function MachineStrip({ machine }: { machine: MachineTelemetrySample }) {
  const readings: { label: string; value: string; tone: LedTone }[] = [
    {
      label: "CPU",
      value: `${machine.cpuPercent.toFixed(0)}%`,
      tone: machine.cpuPercent >= MACHINE_WARN_PERCENT ? "warn" : "idle"
    },
    {
      label: "Memory",
      value: machine.totalMemoryBytes
        ? `${formatBytes(machine.memoryBytes)} of ${formatBytes(machine.totalMemoryBytes)}`
        : formatBytes(machine.memoryBytes),
      tone: (machine.memoryPercent ?? 0) >= MACHINE_WARN_PERCENT ? "warn" : "idle"
    },
    {
      label: "Deluno disk",
      value: `${formatBytes(machine.processReadBytesPerSecond + machine.processWriteBytesPerSecond)}/s`,
      tone: "idle"
    },
    {
      label: "Library drive",
      value: describeDrive(machine),
      // Null busy is not zero busy: the volume can refuse the reading, and an
      // absent figure has to read as "not measured" rather than "idle".
      tone: (machine.diskBusyPercent ?? 0) >= MACHINE_WARN_PERCENT ? "warn" : "idle"
    }
  ];

  return (
    <div className="relative flex flex-wrap items-center gap-x-4 gap-y-1 border-t border-hairline px-[var(--card-pad-x)] py-1.5">
      {readings.map((reading) => (
        <span key={reading.label} className="flex items-center gap-1.5 text-[length:var(--type-micro)]">
          <StatusLed tone={reading.tone} size={5} />
          <span className="text-muted-foreground">{reading.label}</span>
          <span className={cn("font-medium tabular-nums", reading.tone === "warn" ? "text-warning" : "text-foreground")}>
            {reading.value}
          </span>
        </span>
      ))}
    </div>
  );
}

/**
 * The whole volume, including everything else on the machine — which is the
 * half that tells you the slow import is not Deluno's doing. Absent when the
 * volume refuses the reading, and absent has to look different from idle.
 */
function describeDrive(machine: MachineTelemetrySample) {
  if (machine.diskBusyPercent === null) {
    return "not measured";
  }

  const total = (machine.diskReadBytesPerSecond ?? 0) + (machine.diskWriteBytesPerSecond ?? 0);
  return `${formatBytes(total)}/s · ${machine.diskBusyPercent.toFixed(0)}% busy`;
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

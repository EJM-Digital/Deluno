/**
 * Sources and clients page
 *
 * Three goals the old page missed:
 *   1. Adding an indexer should be guided: pick protocol, fill URL + key, test, done.
 *   2. Download clients must have SEPARATE movie and TV categories so they never conflict.
 *   3. Routing must filter by media type: a TV-only indexer should never appear
 *      as an option for a movies library.
 */

import { useState, useMemo, useRef } from "react";
import { Link, useLoaderData, useLocation, useNavigation, useRevalidator } from "react-router-dom";
import {
  AlertTriangle,
  BadgeCheck,
  Cable,
  Check,
  ChevronRight,
  Film,
  HelpCircle,
  Loader2,
  Plus,
  Radio,
  RadioTower,
  RefreshCw,
  Route,
  ShieldAlert,
  Trash2,
  Tv,
  Wifi,
  WifiOff,
  X,
} from "lucide-react";
import {
  fetchJson,
  readValidationProblem,
  type DownloadClientItem,
  type DownloadClientPathMappingItem,
  type ImportExecuteResponse,
  type ImportJobResponse,
  type ImportPreviewRequest,
  type ImportPreviewResponse,
  type DownloadQueueItem,
  type DownloadTelemetryOverview,
  type IndexerItem,
  type LibraryItem,
  type LibraryRoutingSnapshot,
  type PlatformSettingsSnapshot,
  type TagItem,
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { downloadQueueStatuses, queueStatusLabel, telemetryCapabilityChips } from "../lib/download-telemetry";
import { cn } from "../lib/utils";
import { KpiCard } from "../components/app/kpi-card";
import { OperationPathBanner } from "../components/app/operations-guide";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Input } from "../components/ui/input";
import { PresetField } from "../components/ui/preset-field";
import { EmptyState } from "../components/shell/empty-state";
import { CardSkeleton, RowSkeleton } from "../components/shell/skeleton";
import { Stagger, StaggerItem } from "../components/shell/motion";
import { toast } from "../components/shell/toaster";
import { useSignalREvent } from "../lib/use-signalr";

const PRIORITY_OPTIONS = [
  { label: "Highest priority (1)", value: "1" },
  { label: "High priority (5)", value: "5" },
  { label: "Normal priority (10)", value: "10" },
  { label: "Low priority (25)", value: "25" },
  { label: "Fallback only (50)", value: "50" }
];

const HOST_OPTIONS = [
  { label: "This machine (localhost)", value: "localhost" },
  { label: "Localhost IPv4 (127.0.0.1)", value: "127.0.0.1" },
  { label: "Docker host (host.docker.internal)", value: "host.docker.internal" }
];

const CATEGORY_OPTIONS = [
  { label: "Deluno movies", value: "deluno-movies" },
  { label: "Movies", value: "Movies" },
  { label: "movies", value: "movies" },
  { label: "radarr", value: "radarr" }
];

const TV_CATEGORY_OPTIONS = [
  { label: "Deluno TV", value: "deluno-tv" },
  { label: "TV", value: "TV" },
  { label: "tv", value: "tv" },
  { label: "sonarr", value: "sonarr" }
];

/* ── Loader ───────────────────────────────────────────────────────── */
interface LoaderData {
  clients: DownloadClientItem[];
  pathMappings: DownloadClientPathMappingItem[];
  indexers: IndexerItem[];
  libraries: LibraryItem[];
  routing: LibraryRoutingSnapshot[];
  settings: PlatformSettingsSnapshot;
  tags: TagItem[];
  telemetry: DownloadTelemetryOverview | null;
}

export async function indexersLoader(): Promise<LoaderData> {
  const [indexers, clients, libraries, settings, tags, telemetry] = await Promise.all([
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<DownloadClientItem[]>("/api/download-clients"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<TagItem[]>("/api/tags"),
    fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry").catch(() => null),
  ]);
  const routing = await Promise.all(
    libraries.map((lib) =>
      fetchJson<LibraryRoutingSnapshot>(`/api/libraries/${lib.id}/routing`).catch(() => ({
        libraryId: lib.id,
        libraryName: lib.name,
        sources: [],
        downloadClients: [],
      }))
    )
  );
  const pathMappings = (await Promise.all(
    clients.map((client) => fetchJson<DownloadClientPathMappingItem[]>(`/api/download-clients/${client.id}/path-mappings`).catch(() => []))
  )).flat();
  return { clients, pathMappings, indexers, libraries, routing, settings, tags, telemetry };
}

/* ── Indexer protocol presets ─────────────────────────────────────── */
type IndexerProtocol = "torznab" | "newznab" | "rss" | "custom";
type MediaScope = "movies" | "tv" | "both";

interface IndexerPreset {
  protocol: IndexerProtocol;
  label: string;
  description: string;
  icon: string;
  defaultCategories: (scope: MediaScope) => string;
  requiresApiKey: boolean;
}

const INDEXER_PRESETS: IndexerPreset[] = [
  {
    protocol: "torznab",
    label: "Torznab",
    description: "Any Torznab-compatible tracker or private site. Works with qBittorrent, Deluge, and Transmission.",
    icon: "⚡",
    requiresApiKey: true,
    defaultCategories: (scope) =>
      scope === "movies" ? "2000,2010,2020,2030,2040,2045,2050,2060,2070" :
      scope === "tv"     ? "5000,5010,5020,5030,5040,5045,5050,5060,5070" :
                           "2000,2010,2020,2030,2040,2045,2050,2060,5000,5010,5020,5030,5040,5045,5050",
  },
  {
    protocol: "newznab",
    label: "Newznab",
    description: "NZBGeek, DrunkenSlug, NZBCat, or any Newznab-compatible Usenet indexer.",
    icon: "📰",
    requiresApiKey: true,
    defaultCategories: (scope) =>
      scope === "movies" ? "2000,2010,2020,2030,2040,2045,2050,2060,2070" :
      scope === "tv"     ? "5000,5010,5020,5030,5040,5045,5050,5060,5070" :
                           "2000,2010,2020,2030,2040,2045,2050,2060,5000,5010,5020,5030,5040,5045,5050",
  },
  {
    protocol: "rss",
    label: "RSS Feed",
    description: "Plain RSS feed without authentication. Useful for public trackers or custom feeds.",
    icon: "📡",
    requiresApiKey: false,
    defaultCategories: () => "",
  },
  {
    protocol: "custom",
    label: "Custom",
    description: "Manual configuration for indexers not covered by the presets above.",
    icon: "⚙️",
    requiresApiKey: false,
    defaultCategories: () => "",
  },
];

const MEDIA_SCOPE_OPTIONS: { id: MediaScope; label: string; description: string; icon: typeof Film }[] = [
  { id: "both",   label: "Movies + TV",  description: "Searches both; the most common choice", icon: Radio },
  { id: "movies", label: "Movies only",  description: "Only used for movie searches",            icon: Film  },
  { id: "tv",     label: "TV only",      description: "Only used for TV series searches",        icon: Tv    },
];

/* Download client type presets */
interface ClientPreset {
  protocol: string;
  label: string;
  description: string;
  icon: string;
  defaultPort: number;
  isUsenet: boolean;
  defaultMoviesCategory: string;
  defaultTvCategory: string;
  authMode: string;
  supportsRecheck: boolean;
  supportsImportPath: boolean;
  setupHint: string;
}

const CLIENT_PRESETS: ClientPreset[] = [
  {
    protocol: "qbittorrent",
    label: "qBittorrent",
    description: "The most popular torrent client. Free, open source, feature-rich.",
    icon: "QB",
    defaultPort: 8080,
    isUsenet: false,
    defaultMoviesCategory: "deluno-movies",
    defaultTvCategory: "deluno-tv",
    authMode: "Web login",
    supportsRecheck: true,
    supportsImportPath: true,
    setupHint: "Enable the Web UI and use the same username and password you use in qBittorrent.",
  },
  {
    protocol: "transmission",
    label: "Transmission",
    description: "Lightweight torrent client. Popular on Linux and NAS devices.",
    icon: "TR",
    defaultPort: 9091,
    isUsenet: false,
    defaultMoviesCategory: "deluno-movies",
    defaultTvCategory: "deluno-tv",
    authMode: "Basic auth",
    supportsRecheck: true,
    supportsImportPath: true,
    setupHint: "Use the RPC port. Deluno handles the Transmission session token automatically.",
  },
  {
    protocol: "deluge",
    label: "Deluge",
    description: "Thin-client based torrent client. Highly customisable.",
    icon: "DL",
    defaultPort: 8112,
    isUsenet: false,
    defaultMoviesCategory: "deluno-movies",
    defaultTvCategory: "deluno-tv",
    authMode: "Password",
    supportsRecheck: true,
    supportsImportPath: true,
    setupHint: "Use the Deluge Web UI password. Labels are used as Deluno categories.",
  },
  {
    protocol: "utorrent",
    label: "uTorrent",
    description: "Legacy Web UI client support for users migrating existing setups.",
    icon: "uT",
    defaultPort: 8080,
    isUsenet: false,
    defaultMoviesCategory: "deluno-movies",
    defaultTvCategory: "deluno-tv",
    authMode: "Token auth",
    supportsRecheck: true,
    supportsImportPath: false,
    setupHint: "Use the Web UI credentials. uTorrent may not expose a reliable finished path, so imports can need the shared downloads path.",
  },
  {
    protocol: "sabnzbd",
    label: "SABnzbd",
    description: "The leading Usenet downloader. Requires a Usenet provider subscription.",
    icon: "SAB",
    defaultPort: 8080,
    isUsenet: true,
    defaultMoviesCategory: "Movies",
    defaultTvCategory: "TV",
    authMode: "API key",
    supportsRecheck: false,
    supportsImportPath: true,
    setupHint: "Paste the SABnzbd API key into the password field. Categories map directly to SABnzbd folders.",
  },
  {
    protocol: "nzbget",
    label: "NZBGet",
    description: "Efficient Usenet downloader. Lower resource usage than SABnzbd.",
    icon: "NZB",
    defaultPort: 6789,
    isUsenet: true,
    defaultMoviesCategory: "Movies",
    defaultTvCategory: "TV",
    authMode: "Basic auth",
    supportsRecheck: false,
    supportsImportPath: true,
    setupHint: "Use NZBGet username and password. Deluno reads queue groups, history, download rate, and destination folders.",
  },
];

/* Health helpers */
function healthVariant(v: string): "success" | "warning" | "destructive" {
  return v === "healthy" ? "success" : v === "degraded" || v === "untested" ? "warning" : "destructive";
}

function healthLabel(v: string) {
  if (v === "healthy") return "Healthy";
  if (v === "untested") return "Not tested yet";
  if (v === "degraded") return "Degraded";
  if (v === "disabled") return "Disabled";
  if (v === "unreachable") return "Unreachable";
  return "Offline";
}

function actionLabel(action: "pause" | "resume" | "delete" | "recheck") {
  return {
    pause: "Pause",
    resume: "Resume",
    delete: "Remove",
    recheck: "Recheck"
  }[action];
}

function formatClientHistoryTime(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "unknown";
  return date.toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  });
}

function buildImportRequest(item: DownloadQueueItem, downloadsPath: string | null): ImportPreviewRequest {
  const fileName = inferImportFileName(item);
  const sourceBase = downloadsPath?.trim() || "D:\\Downloads";
  const sourcePath = sourceBase.endsWith("\\") || sourceBase.endsWith("/")
    ? `${sourceBase}${fileName}`
    : `${sourceBase}\\${fileName}`;

  return {
    sourcePath,
    fileName,
    mediaType: item.mediaType,
    title: item.title,
    year: inferYear(item.releaseName),
    genres: [],
    tags: [item.category].filter(Boolean),
    studio: null,
    originalLanguage: null
  };
}

function inferImportFileName(item: DownloadQueueItem) {
  const normalized = item.releaseName
    .replace(/[<>:"/\\|?*]+/g, ".")
    .replace(/\s+/g, ".")
    .replace(/\.+/g, ".")
    .replace(/^\.+|\.+$/g, "");
  return /\.(mkv|mp4|avi|mov|m4v)$/i.test(normalized) ? normalized : `${normalized || item.id}.mkv`;
}

function inferYear(value: string) {
  const match = value.match(/\b(19|20)\d{2}\b/);
  return match ? Number(match[0]) : null;
}

function QueueActionButton({
  action,
  busyKey,
  clientId,
  item,
  onAction
}: {
  action: "pause" | "resume" | "delete" | "recheck";
  busyKey: string | null;
  clientId: string;
  item: DownloadQueueItem;
  onAction: (clientId: string, item: DownloadQueueItem, action: "pause" | "resume" | "delete" | "recheck") => Promise<void>;
}) {
  const key = `queue:${clientId}:${item.id}:${action}`;
  return (
    <Button
      type="button"
      size="sm"
      variant={action === "delete" ? "ghost" : "outline"}
      onClick={() => void onAction(clientId, item, action)}
      disabled={busyKey !== null}
      className="h-7 px-2 text-[10.5px]"
    >
      {busyKey === key ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
      {actionLabel(action)}
    </Button>
  );
}

/* Section header */
function ImportPreviewPanel({ preview }: { preview: ImportPreviewResponse }) {
  const hasWarnings = preview.warnings.length > 0;
  const risk = getImportPreviewRisk(preview);
  const probeSummary = formatProbeSummary(preview.mediaProbe);
  return (
    <div
      className={cn(
        "mt-2 rounded-lg border px-2.5 py-2",
        risk.tone === "blocked"
          ? "border-destructive/30 bg-destructive/5"
          : risk.tone === "warning"
            ? "border-warning/25 bg-warning/5"
            : "border-primary/20 bg-primary/5"
      )}
    >
      <div className="flex flex-wrap items-center gap-2">
        <p
          className={cn(
            "text-[10px] font-semibold uppercase tracking-[0.14em]",
            risk.tone === "blocked" ? "text-destructive" : risk.tone === "warning" ? "text-warning" : "text-primary"
          )}
        >
          Import route - {preview.preferredTransferMode}
        </p>
        <Badge variant={risk.badgeVariant} className="text-[9px]">
          {risk.label}
        </Badge>
        <Badge variant={preview.sourceExists ? "success" : "destructive"} className="text-[9px]">
          source {preview.sourceExists ? "visible" : "missing"}
        </Badge>
        <Badge variant={preview.destinationExists ? "warning" : "success"} className="text-[9px]">
          destination {preview.destinationExists ? "exists" : "clear"}
        </Badge>
      </div>
      <p className="mt-1 break-all font-mono text-[10px] text-muted-foreground">
        {preview.destinationPath}
      </p>
      <p className="mt-1 text-[10.5px] text-muted-foreground">
        {preview.explanation} {preview.transferExplanation}
      </p>
      {probeSummary ? (
        <p className="mt-1 font-mono text-[10px] text-muted-foreground">
          {probeSummary}
        </p>
      ) : null}
      {preview.decisionSteps.length ? (
        <div className="mt-2 rounded-md border border-hairline bg-background/40 p-2">
          <p className="text-[9px] font-bold uppercase tracking-[0.16em] text-muted-foreground">Decision path</p>
          <ol className="mt-1 space-y-1">
            {preview.decisionSteps.map((step, index) => (
              <li key={`${index}-${step}`} className="grid grid-cols-[16px_minmax(0,1fr)] gap-1.5 text-[10.5px] text-muted-foreground">
                <span className="font-mono text-primary">{index + 1}</span>
                <span>{step}</span>
              </li>
            ))}
          </ol>
        </div>
      ) : null}
      {hasWarnings ? (
        <div className="mt-2 space-y-1">
          {preview.warnings.map((warning) => (
            <p key={warning} className="flex gap-1.5 text-[10.5px] text-warning">
              <AlertTriangle className="mt-0.5 h-3 w-3 shrink-0" />
              <span>{warning}</span>
            </p>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function getImportPreviewRisk(preview: ImportPreviewResponse) {
  const warnings = preview.warnings.map((warning) => warning.toLowerCase());
  const isBlocked =
    !preview.sourceExists ||
    !preview.isSupportedMediaFile ||
    warnings.some((warning) => warning.includes("same file") || warning.includes("same path"));
  const isWarning = preview.destinationExists || warnings.length > 0;
  if (isBlocked) return { label: "Blocked", tone: "blocked" as const, badgeVariant: "destructive" as const };
  if (isWarning) return { label: "Review", tone: "warning" as const, badgeVariant: "warning" as const };
  return { label: "Ready", tone: "ready" as const, badgeVariant: "success" as const };
}

function formatProbeSummary(probe: ImportPreviewResponse["mediaProbe"]) {
  if (!probe) return "";
  const parts = [`Probe: ${probe.status}`];
  if (probe.durationSeconds) parts.push(formatDuration(probe.durationSeconds));
  const video = probe.videoStreams[0];
  if (video) parts.push(`${video.codec ?? "video"} ${video.width ?? "?"}x${video.height ?? "?"}`);
  parts.push(`${probe.audioStreams.length} audio`);
  parts.push(`${probe.subtitleStreams.length} subs`);
  return parts.join(" - ");
}

function formatDuration(seconds: number) {
  const rounded = Math.max(0, Math.round(seconds));
  const h = Math.floor(rounded / 3600).toString().padStart(2, "0");
  const m = Math.floor((rounded % 3600) / 60).toString().padStart(2, "0");
  const s = (rounded % 60).toString().padStart(2, "0");
  return `${h}:${m}:${s}`;
}

function SectionHeader({ icon: Icon, title, meta, action }: {
  icon: typeof Film;
  title: string;
  meta: string;
  action?: React.ReactNode;
}) {
  return (
    <div className="flex items-start justify-between gap-3">
      <div className="flex items-center gap-3">
        <div className="flex h-9 w-9 items-center justify-center rounded-xl bg-surface-2">
          <Icon className="h-4.5 w-4.5 text-muted-foreground" />
        </div>
        <div>
          <h3 className="font-semibold text-foreground">{title}</h3>
          <p className="text-[12px] text-muted-foreground">{meta}</p>
        </div>
      </div>
      {action}
    </div>
  );
}

/* ── Media scope badge ────────────────────────────────────────────── */
function ScopeBadge({ scope }: { scope?: string | null }) {
  if (!scope || scope === "both") return (
    <span className="rounded-full border border-hairline px-2 py-0.5 text-[10px] font-semibold text-muted-foreground">Both</span>
  );
  if (scope === "movies") return (
    <span className="rounded-full border border-sky-500/20 bg-sky-500/10 px-2 py-0.5 text-[10px] font-semibold text-sky-400">Movies</span>
  );
  return (
    <span className="rounded-full border border-violet-500/20 bg-violet-500/10 px-2 py-0.5 text-[10px] font-semibold text-violet-400">TV</span>
  );
}

/* ── Toggle switch ────────────────────────────────────────────────── */
function Toggle({ checked, onChange, ariaLabel }: { checked: boolean; onChange: (v: boolean) => void; ariaLabel?: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-label={ariaLabel}
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className={cn(
        "relative inline-flex h-6 w-11 shrink-0 rounded-full border-2 border-transparent transition-colors",
        checked ? "bg-primary" : "bg-muted-foreground/30"
      )}
    >
      <span className={cn("pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow-lg transition-transform", checked ? "translate-x-5" : "translate-x-0")} />
    </button>
  );
}

/* ── Inline help ──────────────────────────────────────────────────── */
function Help({ text }: { text: string }) {
  const [open, setOpen] = useState(false);
  return (
    <span className="relative inline-block">
      <button
        type="button"
        onMouseEnter={() => setOpen(true)}
        onMouseLeave={() => setOpen(false)}
        className="text-muted-foreground/40 hover:text-muted-foreground"
      >
        <HelpCircle className="h-3.5 w-3.5" />
      </button>
      {open && (
        <span className="absolute bottom-full left-1/2 z-50 mb-2 w-60 -translate-x-1/2 rounded-xl border border-hairline bg-popover px-3 py-2 text-[11.5px] leading-relaxed text-muted-foreground shadow-xl">
          {text}
        </span>
      )}
    </span>
  );
}

/* ── Indexer add wizard ───────────────────────────────────────────── */
function PresetCapabilityChip({ label, enabled }: { label: string; enabled: boolean }) {
  return (
    <span
      className={cn(
        "rounded-full border px-2 py-0.5 text-[10px] font-medium",
        enabled
          ? "border-primary/25 bg-primary/8 text-primary"
          : "border-hairline bg-background/40 text-muted-foreground"
      )}
    >
      {enabled ? label : `No ${label.toLowerCase()}`}
    </span>
  );
}

function HealthFact({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0 rounded-lg border border-hairline bg-background/30 px-2 py-1">
      <span className="font-semibold uppercase tracking-[0.12em] text-muted-foreground/70">{label}</span>
      <span className="ml-1 font-mono text-muted-foreground">{value}</span>
    </div>
  );
}

function IndexerAddPanel({ onSave, onCancel }: {
  onSave: (data: Record<string, unknown>) => Promise<void>;
  onCancel: () => void;
}) {
  const [protocol, setProtocol] = useState<IndexerProtocol | null>(null);
  const [scope, setScope] = useState<MediaScope>("both");
  const [name, setName] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [priority, setPriority] = useState(1);
  const [saving, setSaving] = useState(false);

  const preset = INDEXER_PRESETS.find((p) => p.protocol === protocol);
  const autoCategories = preset ? preset.defaultCategories(scope) : "";

  async function handleSave() {
    if (!name.trim() || !baseUrl.trim() || !protocol) return;
    setSaving(true);
    try {
      await onSave({
        name,
        protocol,
        privacy: "private",
        baseUrl,
        apiKey: apiKey || undefined,
        priority,
        categories: autoCategories,
        tags: "",
        isEnabled: true,
        mediaScope: scope,
      });
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="space-y-[var(--page-gap)] rounded-2xl border border-primary/20 bg-surface-1 p-[var(--tile-pad)] shadow-[0_0_40px_hsl(var(--primary)/0.06)]">
      <div className="flex items-center justify-between">
        <div>
          <p className="font-semibold text-foreground">Connect an indexer</p>
          <p className="text-[12px] text-muted-foreground">Choose a type, fill in the details, done.</p>
        </div>
        <button type="button" onClick={onCancel} className="rounded-xl p-1.5 text-muted-foreground hover:bg-muted/30">
          <X className="h-4 w-4" />
        </button>
      </div>

      {/* Step 1: Protocol */}
      <div className="space-y-2">
        <p className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Protocol</p>
        <div className="grid gap-2 sm:grid-cols-2">
          {INDEXER_PRESETS.map((p) => (
            <button
              key={p.protocol}
              type="button"
              onClick={() => setProtocol(p.protocol)}
              className={cn(
                "flex items-start gap-3 rounded-2xl border p-[calc(var(--tile-pad)*0.7)] text-left transition-all",
                protocol === p.protocol
                  ? "border-primary/30 bg-primary/5 shadow-[0_0_0_2px_hsl(var(--primary)/0.15)]"
                  : "border-hairline hover:border-primary/20"
              )}
            >
              <span className="text-lg leading-none">{p.icon}</span>
              <div className="min-w-0">
                <p className="text-[13px] font-semibold text-foreground">{p.label}</p>
                <p className="mt-0.5 text-[11.5px] text-muted-foreground line-clamp-2">{p.description}</p>
              </div>
              {protocol === p.protocol && (
                <Check className="ml-auto h-4 w-4 shrink-0 text-primary" />
              )}
            </button>
          ))}
        </div>
      </div>

      {protocol && (
        <>
          {/* Step 2: Media scope */}
          <div className="space-y-2">
            <p className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground flex items-center gap-1.5">
              What should Deluno search here?
              <Help text="This determines which searches use this source. Deluno handles the matching categories automatically." />
            </p>
            <div className="flex gap-2">
              {MEDIA_SCOPE_OPTIONS.map((opt) => (
                <button
                  key={opt.id}
                  type="button"
                  onClick={() => setScope(opt.id)}
                  className={cn(
                    "flex flex-1 items-center gap-2 rounded-xl border px-3 py-2.5 text-left transition-all",
                    scope === opt.id
                      ? "border-primary/30 bg-primary/8 text-foreground"
                      : "border-hairline text-muted-foreground hover:border-primary/20"
                  )}
                >
                  <opt.icon className="h-4 w-4 shrink-0" />
                  <div>
                    <p className="text-[12.5px] font-medium">{opt.label}</p>
                    <p className="text-[10.5px] text-muted-foreground">{opt.description}</p>
                  </div>
                </button>
              ))}
            </div>
          </div>

          {/* Step 3: Details */}
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Display name</label>
              <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. My Private Tracker" className="h-10" />
            </div>
            <div className="space-y-1.5">
              <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Priority</label>
              <PresetField inputType="number" value={String(priority)} onChange={(value) => setPriority(Number(value || 10))} options={PRIORITY_OPTIONS} customLabel="Custom priority" customPlaceholder="1-50" />
            </div>
            <div className="space-y-1.5 sm:col-span-2">
              <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">
                {protocol === "torznab" ? "Torznab URL" : protocol === "newznab" ? "Newznab URL" : "Base URL"}
              </label>
              <Input
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.target.value)}
                placeholder={
                  protocol === "torznab" ? "http://localhost:9117/api/v2.0/indexers/XXX/results/torznab/" :
                  protocol === "newznab" ? "https://api.nzbgeek.info" :
                  "https://example.com"
                }
                className="h-10 font-mono text-[12.5px]"
              />
            </div>
            {preset?.requiresApiKey && (
              <div className="space-y-1.5 sm:col-span-2">
                <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground flex items-center gap-1.5">
                  API Key
                  <Help text="Found in your indexer's settings page. Required for authentication." />
                </label>
                <Input
                  type="password"
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                  placeholder="Paste your API key"
                  className="h-10 font-mono"
                />
              </div>
            )}
          </div>

          {/* Auto-categories preview */}
          {autoCategories && (
            <div className="flex items-start gap-2 rounded-xl border border-hairline bg-muted/20 px-3 py-2.5">
              <Check className="mt-0.5 h-3.5 w-3.5 shrink-0 text-success" />
              <p className="text-[11.5px] text-muted-foreground">
                <strong className="text-foreground">Categories auto-filled:</strong> {autoCategories}
              </p>
            </div>
          )}

          <div className="flex justify-end gap-2">
            <Button variant="ghost" onClick={onCancel}>Cancel</Button>
            <Button
              onClick={() => void handleSave()}
              disabled={!name.trim() || !baseUrl.trim() || saving}
              className="gap-2"
            >
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />}
              {saving ? "Connecting…" : "Connect source"}
            </Button>
          </div>
        </>
      )}
    </div>
  );
}

/* ── Download client add wizard ───────────────────────────────────── */
function ClientAddPanel({ onSave, onCancel }: {
  onSave: (data: Record<string, unknown>) => Promise<void>;
  onCancel: () => void;
}) {
  const [protocol, setProtocol] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [host, setHost] = useState("localhost");
  const [port, setPort] = useState(8080);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [moviesCategory, setMoviesCategory] = useState("deluno-movies");
  const [tvCategory, setTvCategory] = useState("deluno-tv");
  const [saving, setSaving] = useState(false);

  const preset = CLIENT_PRESETS.find((p) => p.protocol === protocol);

  function selectPreset(p: ClientPreset) {
    setProtocol(p.protocol);
    setPort(p.defaultPort);
    setMoviesCategory(p.defaultMoviesCategory);
    setTvCategory(p.defaultTvCategory);
    if (!name) setName(p.label);
  }

  async function handleSave() {
    if (!name.trim() || !host.trim() || !protocol) return;
    setSaving(true);
    try {
      await onSave({
        name,
        protocol,
        host,
        port,
        username: username || undefined,
        password: password || undefined,
        endpointUrl: `http://${host}:${port}`,
        moviesCategory,
        tvCategory,
        categoryTemplate: moviesCategory,
        priority: 1,
        isEnabled: true,
      });
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="space-y-[var(--page-gap)] rounded-2xl border border-primary/20 bg-surface-1 p-[var(--tile-pad)] shadow-[0_0_40px_hsl(var(--primary)/0.06)]">
      <div className="flex items-center justify-between">
        <div>
          <p className="font-semibold text-foreground">Connect downloads</p>
          <p className="text-[12px] text-muted-foreground">Movies and TV will be sent to separate categories automatically.</p>
        </div>
        <button type="button" onClick={onCancel} className="rounded-xl p-1.5 text-muted-foreground hover:bg-muted/30">
          <X className="h-4 w-4" />
        </button>
      </div>

      {/* Client type picker */}
      <div className="space-y-2">
        <p className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Download client</p>
        <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          {CLIENT_PRESETS.map((p) => (
            <button
              key={p.protocol}
              type="button"
              onClick={() => selectPreset(p)}
              className={cn(
                "flex items-center gap-2.5 rounded-2xl border p-3 text-left transition-all",
                protocol === p.protocol
                  ? "border-primary/30 bg-primary/5 shadow-[0_0_0_2px_hsl(var(--primary)/0.15)]"
                  : "border-hairline hover:border-primary/20"
              )}
            >
              <span className="text-xl leading-none">{p.icon}</span>
              <div className="min-w-0 flex-1">
                <p className="text-[12.5px] font-semibold text-foreground leading-tight">{p.label}</p>
                <p className="text-[10px] text-muted-foreground">{p.isUsenet ? "Usenet" : "Torrent"} / {p.authMode}</p>
              </div>
              {protocol === p.protocol && <Check className="h-3.5 w-3.5 shrink-0 text-primary" />}
            </button>
          ))}
        </div>
      </div>

      {protocol && (
        <>
          {preset ? (
            <div className="grid gap-3 rounded-2xl border border-hairline bg-surface-2 p-4 lg:grid-cols-[minmax(0,1fr)_auto]">
              <div className="min-w-0">
                <p className="text-[12.5px] font-semibold text-foreground">{preset.label}</p>
                <p className="mt-1 text-[12px] text-muted-foreground">{preset.description}</p>
                <p className="mt-1 text-[11.5px] text-muted-foreground">{preset.setupHint}</p>
              </div>
              <div className="flex flex-wrap content-start gap-1.5 lg:max-w-[360px] lg:justify-end">
                <PresetCapabilityChip label="Queue telemetry" enabled />
                <PresetCapabilityChip label="History" enabled />
                <PresetCapabilityChip label="Pause/resume" enabled />
                <PresetCapabilityChip label="Remove" enabled />
                <PresetCapabilityChip label="Recheck" enabled={preset.supportsRecheck} />
                <PresetCapabilityChip label="Import path" enabled={preset.supportsImportPath} />
                <PresetCapabilityChip label={preset.authMode} enabled />
              </div>
            </div>
          ) : null}

          {/* Connection details */}
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Display name</label>
              <Input value={name} onChange={(e) => setName(e.target.value)} className="h-10" />
            </div>
            <div className="space-y-1.5">
              <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Port</label>
              <PresetField inputType="number" value={String(port)} onChange={(value) => setPort(Number(value || preset?.defaultPort || 8080))} options={CLIENT_PRESETS.map((client) => ({ label: `${client.label} default (${client.defaultPort})`, value: String(client.defaultPort) }))} customLabel="Custom port" customPlaceholder="Port number" />
            </div>
            <div className="space-y-1.5">
              <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Host / IP</label>
              <PresetField value={host} onChange={setHost} options={HOST_OPTIONS} customLabel="Custom host / IP" customPlaceholder="Hostname or IP address" />
            </div>
            <div className="space-y-1.5">
              <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Username (optional)</label>
              <Input value={username} onChange={(e) => setUsername(e.target.value)} className="h-10" />
            </div>
            <div className="space-y-1.5 sm:col-span-2">
              <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Password (optional)</label>
              <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} className="h-10" />
            </div>
          </div>

          {/* ── KEY FEATURE: Separate movies/TV categories ── */}
          <div className="rounded-2xl border border-hairline bg-surface-2 p-4 space-y-3">
            <div className="flex items-center gap-2">
              <div className="flex h-5 w-5 items-center justify-center rounded-full bg-success/15">
                <Check className="h-3 w-3 text-success" />
              </div>
              <p className="text-[12.5px] font-semibold text-foreground">Separate categories keep Movies and TV apart</p>
            </div>
            <p className="text-[12px] text-muted-foreground">
              Deluno routes each download to the correct category so your client sorts them into different folders automatically.
              This is the key setting that prevents Movies and TV from conflicting.
            </p>
            <div className="grid gap-3 sm:grid-cols-2">
              <div className="space-y-1.5">
                <label className="flex items-center gap-1.5 text-[11px] font-bold uppercase tracking-[0.14em] text-sky-400">
                  <Film className="h-3 w-3" />
                  Movies category
                  <Help text="Label or category name used in your download client for movie downloads. In qBittorrent this sets the tag; in SABnzbd/NZBGet it sets the category/folder." />
                </label>
                <PresetField
                  value={moviesCategory}
                  onChange={setMoviesCategory}
                  options={CATEGORY_OPTIONS}
                  customLabel="Custom movie category"
                  customPlaceholder="Download-client category"
                />
              </div>
              <div className="space-y-1.5">
                <label className="flex items-center gap-1.5 text-[11px] font-bold uppercase tracking-[0.14em] text-violet-400">
                  <Tv className="h-3 w-3" />
                  TV category
                  <Help text="Label or category name used in your download client for TV show downloads. Should be different from the movies category." />
                </label>
                <PresetField
                  value={tvCategory}
                  onChange={setTvCategory}
                  options={TV_CATEGORY_OPTIONS}
                  customLabel="Custom TV category"
                  customPlaceholder="Download-client category"
                />
              </div>
            </div>
            {moviesCategory === tvCategory && moviesCategory !== "" && (
              <div className="flex items-center gap-2 rounded-xl border border-amber-500/20 bg-amber-500/10 px-3 py-2">
                <AlertTriangle className="h-3.5 w-3.5 shrink-0 text-amber-400" />
                <p className="text-[11.5px] text-amber-400">
                  Movies and TV categories are the same — they will be mixed together in your download client.
                </p>
              </div>
            )}
          </div>

          <div className="flex justify-end gap-2">
            <Button variant="ghost" onClick={onCancel}>Cancel</Button>
            <Button
              onClick={() => void handleSave()}
              disabled={!name.trim() || !host.trim() || saving}
              className="gap-2"
            >
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />}
              {saving ? "Connecting…" : "Connect downloads"}
            </Button>
          </div>
        </>
      )}
    </div>
  );
}

function ClientFileAccess({
  client,
  mappings,
  onAdd,
  onRemove,
  onTest
}: {
  client: DownloadClientItem;
  mappings: DownloadClientPathMappingItem[];
  onAdd: (clientId: string, remotePath: string, localPath: string) => Promise<void>;
  onRemove: (clientId: string, mappingId: string) => Promise<void>;
  onTest: (clientId: string, mappingId: string) => Promise<void>;
}) {
  const [open, setOpen] = useState(false);
  const [remotePath, setRemotePath] = useState("");
  const [localPath, setLocalPath] = useState("");
  const [saving, setSaving] = useState(false);

  async function addMapping() {
    if (!remotePath.trim() || !localPath.trim()) return;
    setSaving(true);
    try {
      await onAdd(client.id, remotePath.trim(), localPath.trim());
      setRemotePath("");
      setLocalPath("");
      setOpen(false);
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="mt-3 rounded-xl border border-hairline bg-surface-1 px-3 py-2.5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <p className="text-[11.5px] font-semibold text-foreground">File locations</p>
          <p className="mt-0.5 text-[10.5px] text-muted-foreground">
            {mappings.length > 0
              ? `${mappings.length} path translation${mappings.length === 1 ? "" : "s"} tells Deluno where this client’s completed files are visible.`
              : "Configure this client’s completed-download folder in the client itself. Nothing else is needed when Deluno can see that same folder."}
          </p>
        </div>
        <Button type="button" size="sm" variant="outline" className="h-7 px-2 text-[10.5px]" onClick={() => setOpen((value) => !value)}>
          {open ? "Done" : mappings.length ? "Manage locations" : "Link different paths"}
        </Button>
      </div>

      {mappings.length > 0 ? (
        <div className="mt-2 space-y-1.5">
          {mappings.map((mapping) => (
            <div key={mapping.id} className="flex min-w-0 items-center gap-2 rounded-lg bg-background/45 px-2.5 py-2 text-[10.5px]">
              <span className="min-w-0 flex-1 truncate font-mono text-muted-foreground" title={mapping.remotePath}>{mapping.remotePath}</span>
              <span aria-hidden="true" className="text-primary">→</span>
              <span className="min-w-0 flex-1 truncate font-mono text-foreground" title={mapping.localPath}>{mapping.localPath}</span>
              <button type="button" onClick={() => void onTest(client.id, mapping.id)} className="rounded px-1.5 py-1 text-[10px] font-semibold text-primary hover:bg-primary/10">
                Test
              </button>
              <button type="button" aria-label={`Remove ${mapping.remotePath} mapping`} onClick={() => void onRemove(client.id, mapping.id)} className="rounded p-1 text-muted-foreground hover:bg-destructive/10 hover:text-destructive">
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            </div>
          ))}
        </div>
      ) : null}

      {open ? (
        <div className="mt-3 grid gap-2 border-t border-hairline pt-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-end">
          <label className="block">
            <span className="text-[10px] font-bold uppercase tracking-[0.12em] text-muted-foreground">Path reported by {client.name}</span>
            <Input value={remotePath} onChange={(event) => setRemotePath(event.target.value)} placeholder="/downloads/complete" className="mt-1 h-8 font-mono text-[11px]" />
          </label>
          <label className="block">
            <span className="text-[10px] font-bold uppercase tracking-[0.12em] text-muted-foreground">Matching path visible to Deluno</span>
            <Input value={localPath} onChange={(event) => setLocalPath(event.target.value)} placeholder="D:\\Downloads\\complete" className="mt-1 h-8 font-mono text-[11px]" />
          </label>
          <Button type="button" size="sm" onClick={() => void addMapping()} disabled={saving || !remotePath.trim() || !localPath.trim()} className="h-8 gap-1.5 text-[10.5px]">
            {saving ? <Loader2 className="h-3 w-3 animate-spin" /> : <Plus className="h-3 w-3" />}
            Link paths
          </Button>
          <p className="text-[10.5px] text-muted-foreground sm:col-span-3">
            Use this only when the same completed files have different paths. Example: a Docker client reports <span className="font-mono">/downloads/complete</span>, while Deluno reads them at <span className="font-mono">D:\\Downloads\\complete</span>. This is the technical “remote path mapping”, explained as a location link.
          </p>
        </div>
      ) : null}
    </div>
  );
}

/* ── Library routing panel ────────────────────────────────────────── */
function LibraryRoutingPanel({
  library,
  routing,
  indexers,
  clients,
  onSave,
  saving,
}: {
  library: LibraryItem;
  routing: LibraryRoutingSnapshot;
  indexers: IndexerItem[];
  clients: DownloadClientItem[];
  onSave: (sourceIds: string[], clientIds: string[]) => Promise<void>;
  saving: boolean;
}) {
  const isTV = library.mediaType === "tv";
  const [sourceIds, setSourceIds] = useState<string[]>(() =>
    routing.sources.map((s) => s.indexerId)
  );
  const [clientIds, setClientIds] = useState<string[]>(() =>
    routing.downloadClients.map((c) => c.downloadClientId)
  );

  // Only show indexers that cover this library's media type
  const relevantIndexers = indexers.filter((idx) => {
    const scope = idx.mediaScope ?? "both";
    return scope === "both" || scope === (isTV ? "tv" : "movies");
  });

  // All clients are always available (they handle routing internally via categories)
  const relevantClients = clients;

  const mediaColor = isTV ? "text-violet-400 border-violet-500/20 bg-violet-500/10" : "text-sky-400 border-sky-500/20 bg-sky-500/10";

  function toggleSource(id: string, checked: boolean) {
    setSourceIds((prev) => checked ? [...prev, id] : prev.filter((x) => x !== id));
  }
  function toggleClient(id: string, checked: boolean) {
    setClientIds((prev) => checked ? [...prev, id] : prev.filter((x) => x !== id));
  }

  return (
    <div className="space-y-[calc(var(--field-group-pad)*0.9)] rounded-2xl border border-hairline bg-surface-1 p-[var(--field-group-pad)]">
      {/* Library header */}
      <div className="flex items-center gap-3">
        <div className={cn("flex h-8 w-8 items-center justify-center rounded-xl border", mediaColor)}>
          {isTV ? <Tv className="h-4 w-4" /> : <Film className="h-4 w-4" />}
        </div>
        <div>
          <p className="font-semibold text-foreground">{library.name}</p>
          <p className="text-[11.5px] text-muted-foreground">
            {sourceIds.length} indexer{sourceIds.length !== 1 ? "s" : ""} · {clientIds.length} download client{clientIds.length !== 1 ? "s" : ""}
          </p>
        </div>
      </div>

      <div className="grid gap-[var(--grid-gap)] md:grid-cols-2">
        {/* Indexers column */}
        <div className="space-y-2">
          <p className="flex items-center gap-1.5 text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">
            Indexers
            <Help text={`Only sources that cover "${isTV ? "TV" : "Movies"}" or "Both" are shown here. Sources limited to the other media type are hidden to prevent mis-routing.`} />
          </p>
          {relevantIndexers.length > 0 ? (
            <div className="space-y-1.5">
              {relevantIndexers.map((idx) => (
                <label
                  key={idx.id}
                  className={cn(
                    "flex items-center gap-3 rounded-xl border px-3 py-2.5 cursor-pointer transition-all",
                    sourceIds.includes(idx.id)
                      ? "border-primary/25 bg-primary/5"
                      : "border-hairline hover:border-primary/15"
                  )}
                >
                  <input
                    type="checkbox"
                    checked={sourceIds.includes(idx.id)}
                    onChange={(e) => toggleSource(idx.id, e.target.checked)}
                    className="accent-primary"
                  />
                  <div className="min-w-0 flex-1">
                    <p className="text-[12.5px] font-medium text-foreground">{idx.name}</p>
                    <p className="text-[10.5px] text-muted-foreground">{idx.protocol.toUpperCase()} · Priority {idx.priority}</p>
                  </div>
                  <Badge variant={healthVariant(idx.healthStatus)} className="text-[9px]">
                    {healthLabel(idx.healthStatus)}
                  </Badge>
                </label>
              ))}
            </div>
          ) : (
            <p className="rounded-xl border border-dashed border-hairline px-3 py-4 text-center text-[12px] text-muted-foreground">
              No {isTV ? "TV" : "movie"}-compatible indexers yet. Connect one above.
            </p>
          )}
        </div>

        {/* Download clients column */}
        <div className="space-y-2">
          <p className="flex items-center gap-1.5 text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">
            Download connections
              <Help text="Deluno uses the movies or TV category configured on each download client. You do not need separate download clients for movies and TV." />
          </p>
          {relevantClients.length > 0 ? (
            <div className="space-y-1.5">
              {relevantClients.map((client) => {
                const cat = isTV
                  ? (client.tvCategory ?? client.categoryTemplate ?? "")
                  : (client.moviesCategory ?? client.categoryTemplate ?? "");
                return (
                  <label
                    key={client.id}
                    className={cn(
                      "flex items-center gap-3 rounded-xl border px-3 py-2.5 cursor-pointer transition-all",
                      clientIds.includes(client.id)
                        ? "border-primary/25 bg-primary/5"
                        : "border-hairline hover:border-primary/15"
                    )}
                  >
                    <input
                      type="checkbox"
                      checked={clientIds.includes(client.id)}
                      onChange={(e) => toggleClient(client.id, e.target.checked)}
                      className="accent-primary"
                    />
                    <div className="min-w-0 flex-1">
                      <p className="text-[12.5px] font-medium text-foreground">{client.name}</p>
                      {cat && (
                        <p className={cn("text-[10.5px] font-mono", isTV ? "text-violet-400" : "text-sky-400")}>
                          → {cat}
                        </p>
                      )}
                    </div>
                    <Badge variant={healthVariant(client.healthStatus)} className="text-[9px]">
                      {healthLabel(client.healthStatus)}
                    </Badge>
                  </label>
                );
              })}
            </div>
          ) : (
            <p className="rounded-xl border border-dashed border-hairline px-3 py-4 text-center text-[12px] text-muted-foreground">
              No download connections yet. Connect one above.
            </p>
          )}
        </div>
      </div>

      <div className="flex items-center justify-between border-t border-hairline pt-3">
        {sourceIds.length === 0 && (
          <p className="flex items-center gap-1.5 text-[11.5px] text-amber-400">
            <AlertTriangle className="h-3.5 w-3.5" />
            No indexers assigned — Deluno cannot look for releases yet
          </p>
        )}
        {clientIds.length === 0 && sourceIds.length > 0 && (
          <p className="flex items-center gap-1.5 text-[11.5px] text-amber-400">
            <AlertTriangle className="h-3.5 w-3.5" />
            No download connection — Deluno cannot send approved releases yet
          </p>
        )}
        <div className="ml-auto">
          <Button size="sm" onClick={() => void onSave(sourceIds, clientIds)} disabled={saving} className="gap-2">
            {saving ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Check className="h-3.5 w-3.5" />}
            Save routing
          </Button>
        </div>
      </div>
    </div>
  );
}

/* ── Page ─────────────────────────────────────────────────────────── */
function IndexersLoadingShell() {
  return (
    <div className="space-y-[var(--page-gap)]" aria-busy="true" aria-live="polite">
      <CardSkeleton />
      <div className="fluid-kpi-grid">
        <CardSkeleton />
        <CardSkeleton />
        <CardSkeleton />
        <CardSkeleton />
      </div>
      <RouteSectionSkeleton />
      <RouteSectionSkeleton />
      <RouteSectionSkeleton />
    </div>
  );
}

function RouteSectionSkeleton() {
  return (
    <div className="rounded-2xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card dark:border-white/[0.07]">
      <RowSkeleton count={4} />
    </div>
  );
}

function ConnectionStartCard({
  to,
  title,
  description,
  action,
  icon: Icon
}: {
  to: string;
  title: string;
  description: string;
  action: string;
  icon: typeof RadioTower;
}) {
  return (
    <Link
      to={to}
      className="group rounded-2xl border border-hairline bg-card p-[var(--tile-pad)] transition hover:border-primary/35 hover:bg-primary/[0.035] dark:border-white/[0.07]"
    >
      <span className="flex h-9 w-9 items-center justify-center rounded-xl bg-primary/10 text-primary">
        <Icon className="h-4 w-4" />
      </span>
      <h2 className="mt-4 text-[15px] font-semibold text-foreground">{title}</h2>
      <p className="mt-1 text-[12px] leading-relaxed text-muted-foreground">{description}</p>
      <span className="mt-4 inline-flex items-center gap-1 text-[11px] font-semibold text-primary">
        {action} <ChevronRight className="h-3.5 w-3.5 transition group-hover:translate-x-0.5" />
      </span>
    </Link>
  );
}

export function IndexersPage() {
  const loaderData = useLoaderData() as LoaderData | undefined;
  const location = useLocation();
  const navigation = useNavigation();
  const revalidator = useRevalidator();
  const lastTelemetryEventRefresh = useRef(0);
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [showIndexerAdd, setShowIndexerAdd] = useState(false);
  const [showClientAdd, setShowClientAdd] = useState(false);
  const [importPreviews, setImportPreviews] = useState<Record<string, ImportPreviewResponse>>({});
  const [pendingQueueRemoval, setPendingQueueRemoval] = useState<DownloadQueueItem | null>(null);

  if (!loaderData) {
    return <IndexersLoadingShell />;
  }

  const { clients, pathMappings, indexers, libraries, routing, settings, tags, telemetry } = loaderData;
  const isRouteLoading = navigation.state !== "idle";
  const activeSection = location.pathname.endsWith("/indexers/indexers")
    ? "indexers"
      : location.pathname.endsWith("/download-clients")
        ? "clients"
      : location.pathname.endsWith("/library-routing")
        ? "routing"
        : "overview";

  useSignalREvent("DownloadProgress", () => {
    const now = Date.now();
    if (revalidator.state === "idle" && now - lastTelemetryEventRefresh.current > 5000) {
      lastTelemetryEventRefresh.current = now;
      revalidator.revalidate();
    }
  });

  const healthyIndexers = indexers.filter((i) => i.healthStatus === "healthy").length;
  const unhealthyCount = [...indexers, ...clients].filter((i) => i.isEnabled && i.healthStatus !== "healthy").length;
  const linkedSources = routing.reduce((n, r) => n + r.sources.length, 0);
  const linkedClients = routing.reduce((n, r) => n + r.downloadClients.length, 0);
  const telemetryByClientId = new Map(telemetry?.clients.map((item) => [item.clientId, item]) ?? []);
  const pathMappingsByClientId = new Map<string, DownloadClientPathMappingItem[]>();
  pathMappings.forEach((mapping) => {
    const items = pathMappingsByClientId.get(mapping.downloadClientId) ?? [];
    items.push(mapping);
    pathMappingsByClientId.set(mapping.downloadClientId, items);
  });

  const movieLibraries = libraries.filter((l) => l.mediaType !== "tv");
  const tvLibraries = libraries.filter((l) => l.mediaType === "tv");

  /* ── Handlers ── */
  async function handleAddIndexer(data: Record<string, unknown>) {
    const res = await authedFetch("/api/indexers", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data),
    });
    if (!res.ok) {
      const prob = await readValidationProblem(res);
      throw new Error(prob?.title ?? "Indexer could not be added.");
    }
    toast.success(`Indexer "${data.name}" added`);
    setShowIndexerAdd(false);
    revalidator.revalidate();
  }

  async function handleAddClient(data: Record<string, unknown>) {
    const res = await authedFetch("/api/download-clients", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data),
    });
    if (!res.ok) {
      const prob = await readValidationProblem(res);
      throw new Error(prob?.title ?? "Download client could not be added.");
    }
    toast.success(`"${data.name}" added`);
    setShowClientAdd(false);
    revalidator.revalidate();
  }

  async function handleDeleteIndexer(id: string, name: string) {
    setBusyKey(`di:${id}`);
    try {
      const res = await authedFetch(`/api/indexers/${id}`, { method: "DELETE" });
      if (!res.ok && res.status !== 204) throw new Error("Could not remove indexer.");
      toast.success(`"${name}" removed`);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Could not remove indexer.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleDeleteClient(id: string, name: string) {
    setBusyKey(`dc:${id}`);
    try {
      const res = await authedFetch(`/api/download-clients/${id}`, { method: "DELETE" });
      if (!res.ok && res.status !== 204) throw new Error("Could not remove client.");
      toast.success(`"${name}" removed`);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Could not remove client.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleTestClient(id: string) {
    setBusyKey(`test-client:${id}`);
    try {
      const res = await authedFetch(`/api/download-clients/${id}/test`, { method: "POST" });
      if (!res.ok) throw new Error("Client test failed.");
      toast.success("Download client reached");
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Client test failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleExternalClientRemovalChange(allowed: boolean) {
    setBusyKey("external-client-removal");
    try {
      const response = await authedFetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...settings, removeCompletedDownloads: allowed })
      });
      if (!response.ok) throw new Error("Could not update queue removal permission.");
      toast.success(allowed ? "Confirmed external-client removal enabled." : "External-client removal disabled.");
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not update queue removal permission.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleQueueAction(clientId: string, item: DownloadQueueItem, action: "pause" | "resume" | "delete" | "recheck") {
    setBusyKey(`queue:${clientId}:${item.id}:${action}`);
    try {
      const res = await authedFetch(`/api/download-clients/${clientId}/queue/actions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action, queueItemId: item.id })
      });
      if (!res.ok) throw new Error("Download action failed.");
      toast.success(`${actionLabel(action)} sent to ${item.clientName}`);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Download action failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleAddPathMapping(clientId: string, remotePath: string, localPath: string) {
    const response = await authedFetch(`/api/download-clients/${clientId}/path-mappings`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ remotePath, localPath, isEnabled: true, priority: 10 })
    });
    if (!response.ok) {
      const problem = await readValidationProblem(response);
      throw new Error(problem?.title ?? "File location link could not be saved.");
    }
    toast.success("File location link added");
    revalidator.revalidate();
  }

  async function handleRemovePathMapping(clientId: string, mappingId: string) {
    const response = await authedFetch(`/api/download-clients/${clientId}/path-mappings/${mappingId}`, { method: "DELETE" });
    if (!response.ok && response.status !== 204) throw new Error("File location link could not be removed.");
    toast.success("File location link removed");
    revalidator.revalidate();
  }

  async function handleTestPathMapping(clientId: string, mappingId: string) {
    const response = await authedFetch(`/api/download-clients/${clientId}/path-mappings/${mappingId}/test`, { method: "POST" });
    if (!response.ok) throw new Error("Deluno could not test this file location.");
    const result = await response.json() as { reachable: boolean; message: string };
    if (result.reachable) toast.success(result.message);
    else toast.error(result.message);
  }

  async function confirmQueueRemoval() {
    if (!pendingQueueRemoval) return;
    const item = pendingQueueRemoval;
    await handleQueueAction(item.clientId, item, "delete");
    setPendingQueueRemoval(null);
  }

  async function handlePreviewImport(item: DownloadQueueItem) {
    const key = `import-preview:${item.clientId}:${item.id}`;
    setBusyKey(key);
    try {
      const request = buildImportRequest(item, settings?.downloadsPath ?? null);
      const preview = await fetchJson<ImportPreviewResponse>("/api/filesystem/import/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request)
      });
      setImportPreviews((current) => ({ ...current, [item.id]: preview }));
      toast.success("Import destination resolved");
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Import preview failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleImportNow(item: DownloadQueueItem) {
    const key = `import-now:${item.clientId}:${item.id}`;
    setBusyKey(key);
    try {
      const request = buildImportRequest(item, settings?.downloadsPath ?? null);
      const result = await fetchJson<ImportExecuteResponse>("/api/filesystem/import/execute", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          preview: request,
          transferMode: "auto",
          overwrite: false,
          allowCopyFallback: true
        })
      });
      setImportPreviews((current) => ({ ...current, [item.id]: result.preview }));
      toast.success(result.message);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Import failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleQueueImport(item: DownloadQueueItem) {
    const key = `import-queue:${item.clientId}:${item.id}`;
    setBusyKey(key);
    try {
      const request = buildImportRequest(item, settings?.downloadsPath ?? null);
      const result = await fetchJson<ImportJobResponse>("/api/filesystem/import/jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          preview: request,
          transferMode: "auto",
          overwrite: false,
          allowCopyFallback: true
        })
      });
      setImportPreviews((current) => ({ ...current, [item.id]: result.preview }));
      toast.success(`Import queued as job ${result.jobId.slice(0, 8)}`);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Import job could not be queued.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleTestIndexer(id: string) {
    setBusyKey(`test:${id}`);
    try {
      const res = await authedFetch(`/api/indexers/${id}/test`, { method: "POST" });
      if (!res.ok) throw new Error("Test failed.");
      toast.success("Indexer test passed");
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Test failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleResetCircuit(id: string, name: string) {
    setBusyKey(`reset:${id}`);
    try {
      const res = await authedFetch(`/api/indexers/${id}/reset-circuit`, { method: "POST" });
      if (!res.ok) throw new Error("Could not reset circuit.");
      toast.success(`Circuit reset for "${name}"`);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Reset failed.");
    } finally {
      setBusyKey(null);
    }
  }


  async function handleToggleIndexer(id: string, name: string, enabled: boolean) {
    setBusyKey(`toggle:${id}`);
    try {
      const res = await authedFetch(`/api/indexers/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ isEnabled: enabled }),
      });
      if (!res.ok) throw new Error("Could not update indexer.");
      toast.success(enabled ? `"${name}" enabled` : `"${name}" disabled`);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Update failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleToggleClient(id: string, name: string, enabled: boolean) {
    setBusyKey(`toggle-client:${id}`);
    try {
      const res = await authedFetch(`/api/download-clients/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ isEnabled: enabled }),
      });
      if (!res.ok) throw new Error("Could not update client.");
      toast.success(enabled ? `"${name}" enabled` : `"${name}" disabled`);
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Update failed.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleSaveRouting(libraryId: string, sourceIds: string[], clientIds: string[]) {
    setBusyKey(`routing:${libraryId}`);
    try {
      const res = await authedFetch(`/api/libraries/${libraryId}/routing`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          sources: sourceIds.map((indexerId, i) => ({ indexerId, priority: i + 1, requiredTags: "", excludedTags: "" })),
          downloadClients: clientIds.map((downloadClientId, i) => ({ downloadClientId, priority: i + 1 })),
        }),
      });
      if (!res.ok) throw new Error("Could not save routing.");
      toast.success("Routing saved");
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Could not save routing.");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      {/* Page header */}
      <div>
        <p className="text-[11px] font-bold uppercase tracking-[0.18em] text-muted-foreground">Connections</p>
        <h1 className="font-display text-3xl font-semibold text-foreground sm:text-4xl">
          {activeSection === "indexers" ? "Indexers" : activeSection === "clients" ? "Download clients" : activeSection === "routing" ? "Library routing" : "Connect Deluno"}
        </h1>
        <p className="mt-1 text-[13px] text-muted-foreground">
          {activeSection === "indexers"
            ? "Connect the services Deluno searches when it needs a release."
            : activeSection === "clients"
              ? "Connect the app that downloads approved releases, then make sure Deluno can see its completed files."
              : activeSection === "routing"
                ? "Choose which indexers and download clients each library is allowed to use."
                : "Set up where Deluno finds releases, where it sends them, and which connections each library can use."}
        </p>
      </div>

      {activeSection === "overview" ? (
        <>
          <Stagger className="fluid-kpi-grid">
            <StaggerItem><KpiCard label="Indexers" value={String(indexers.length)} icon={RadioTower} meta="Services Deluno can search" sparkline={[8,9,9,10,11,12,12,13,14,14,15,15,16,16,17]} /></StaggerItem>
            <StaggerItem><KpiCard label="Ready" value={String(healthyIndexers)} icon={BadgeCheck} meta="Search connections responding normally" delta={unhealthyCount > 0 ? { value: `${unhealthyCount} need review`, tone: "down" } : undefined} sparkline={[18,20,19,21,23,24,24,25,26,25,27,28,28,29,29]} /></StaggerItem>
            <StaggerItem><KpiCard label="Library routing" value={String(linkedSources + linkedClients)} icon={Route} meta="Configured library-to-connection links" sparkline={[3,4,4,5,6,7,6,7,8,9,8,9,10,10,11]} /></StaggerItem>
            <StaggerItem><KpiCard label="Download clients" value={String(clients.length)} icon={Cable} meta="Apps Deluno can send releases to" sparkline={[4,5,4,6,5,6,7,6,7,8,7,8,9,8,9]} /></StaggerItem>
          </Stagger>

          <div className="grid gap-3 lg:grid-cols-3">
            <ConnectionStartCard to="/indexers/indexers" title="Indexers" description="Connect the services that find releases." action="Manage indexers" icon={RadioTower} />
            <ConnectionStartCard to="/indexers/download-clients" title="Download clients" description="Connect SABnzbd, qBittorrent, or another downloader. File locations are clear and visible on each client." action="Manage clients" icon={Cable} />
            <ConnectionStartCard to="/indexers/library-routing" title="Library routing" description="Choose the allowed indexers and clients for every library." action="Set routing" icon={Route} />
          </div>
        </>
      ) : null}

      {/* ── INDEXERS section ── */}
      {activeSection === "indexers" ? <section className="space-y-[var(--page-gap)]">
        <SectionHeader
          icon={RadioTower}
          title="Indexers"
          meta="Services Deluno asks for releases. Each can be used for Movies, TV, or both."
          action={
            <Button onClick={() => { setShowIndexerAdd(true); setShowClientAdd(false); }} className="gap-2" size="sm">
              <Plus className="h-4 w-4" />
              Add indexer
            </Button>
          }
        />

        {showIndexerAdd && (
          <IndexerAddPanel
            onSave={handleAddIndexer}
            onCancel={() => setShowIndexerAdd(false)}
          />
        )}

        {isRouteLoading && indexers.length === 0 ? (
          <RouteSectionSkeleton />
        ) : indexers.length > 0 ? (
          <div className="rounded-2xl border border-hairline overflow-hidden divide-y divide-hairline">
            {indexers.map((idx) => (
              <div key={idx.id} className={cn("group flex items-center gap-3 px-[calc(var(--tile-pad)*0.8)] py-[calc(var(--tile-pad)*0.7)] transition-opacity", !idx.isEnabled && "opacity-60")}>
                {/* Always-visible enable toggle — most important control */}
                <span title={idx.isEnabled ? "Enabled — click to disable" : "Disabled — click to enable"}>
                  <Toggle
                    checked={idx.isEnabled}
                    onChange={(v) => void handleToggleIndexer(idx.id, idx.name, v)}
                  />
                </span>
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <p className={cn("font-medium", idx.isEnabled ? "text-foreground" : "text-muted-foreground")}>{idx.name}</p>
                    <ScopeBadge scope={idx.mediaScope} />
                    <Badge variant={healthVariant(idx.isEnabled ? idx.healthStatus : "disabled")} className="text-[9.5px]">
                      {idx.isEnabled ? healthLabel(idx.healthStatus) : "Disabled"}
                    </Badge>
                    <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-medium uppercase text-muted-foreground">
                      {idx.protocol}
                    </span>
                    <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground">
                      Priority {idx.priority}
                    </span>
                  </div>
                  <p className="mt-0.5 font-mono text-[11px] text-muted-foreground truncate">{idx.baseUrl}</p>
                  {idx.lastHealthMessage && idx.isEnabled && (
                    <p className="mt-0.5 text-[11px] text-muted-foreground">{idx.lastHealthMessage}</p>
                  )}
                  {idx.consecutiveFailures > 0 && idx.isEnabled && (
                    <p className="mt-0.5 text-[11px] text-warning">
                      {idx.consecutiveFailures} consecutive failure{idx.consecutiveFailures !== 1 ? "s" : ""}
                      {idx.disabledReason ? ` — ${idx.disabledReason}` : ""}
                    </p>
                  )}
                </div>
                <div className="flex items-center gap-2 opacity-0 group-hover:opacity-100 transition-opacity">
                  {idx.consecutiveFailures > 0 && idx.isEnabled && (
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => void handleResetCircuit(idx.id, idx.name)}
                      disabled={busyKey === `reset:${idx.id}`}
                      title="Reset the circuit breaker and clear consecutive failure count"
                      className="gap-1.5 border-warning/40 text-warning hover:bg-warning/10"
                    >
                      {busyKey === `reset:${idx.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : <RefreshCw className="h-3 w-3" />}
                      Reset
                    </Button>
                  )}
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => void handleTestIndexer(idx.id)}
                    disabled={busyKey === `test:${idx.id}` || !idx.isEnabled}
                    title={idx.isEnabled ? "Test connectivity to this indexer" : "Enable the indexer before testing"}
                    className="gap-1.5"
                  >
                    {busyKey === `test:${idx.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : <Wifi className="h-3 w-3" />}
                    Test
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => void handleDeleteIndexer(idx.id, idx.name)}
                    disabled={busyKey === `di:${idx.id}`}
                    title="Remove this indexer"
                  >
                    {busyKey === `di:${idx.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Trash2 className="h-3.5 w-3.5 text-muted-foreground" />}
                  </Button>
                </div>
              </div>
            ))}
          </div>
        ) : !showIndexerAdd && !isRouteLoading && (
          <EmptyState
            size="sm"
            variant="custom"
            title="No indexers yet"
            description="Add a Torznab, Newznab, or RSS indexer when you are ready for Deluno to start looking for releases."
            action={
              <Button onClick={() => setShowIndexerAdd(true)} className="gap-2" size="sm">
                <Plus className="h-4 w-4" />
                Add your first indexer
              </Button>
            }
          />
        )}
      </section> : null}

      {/* ── DOWNLOAD CLIENTS section ── */}
      {activeSection === "clients" ? <section className="space-y-[var(--page-gap)]">
        <SectionHeader
          icon={Cable}
          title="Download clients"
          meta="Where Deluno sends approved releases. Each client clearly shows where it saves files and whether Deluno needs a location link to import them."
          action={
            <Button onClick={() => { setShowClientAdd(true); setShowIndexerAdd(false); }} className="gap-2" size="sm">
              <Plus className="h-4 w-4" />
              Connect downloads
            </Button>
          }
        />

        <div className="flex flex-col gap-[var(--grid-gap)] rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)] sm:flex-row sm:items-center sm:justify-between">
          <div className="max-w-3xl">
            <p className="font-semibold text-foreground">Queue removal permission</p>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
              Allow a confirmed <span className="font-medium text-foreground">Remove</span> action for items in external apps such as SABnzbd or qBittorrent. This is for a deliberate manual action only. Automatic failed-download replacement and cleanup are configured separately in Automation &amp; recovery.
            </p>
          </div>
          <div className="flex shrink-0 items-center gap-3 rounded-xl border border-hairline bg-background/45 px-3 py-2">
            <span className="text-sm font-medium text-foreground">{settings.removeCompletedDownloads ? "Allowed" : "Blocked"}</span>
            <Toggle
              checked={settings.removeCompletedDownloads}
              onChange={(allowed) => void handleExternalClientRemovalChange(allowed)}
              ariaLabel="Allow removing client queue entries"
            />
          </div>
        </div>

        {showClientAdd && (
          <ClientAddPanel
            onSave={handleAddClient}
            onCancel={() => setShowClientAdd(false)}
          />
        )}

        {isRouteLoading && clients.length === 0 ? (
          <RouteSectionSkeleton />
        ) : clients.length > 0 ? (
          <div className="rounded-2xl border border-hairline overflow-hidden divide-y divide-hairline">
            {clients.map((client) => {
              const clientTelemetry = telemetryByClientId.get(client.id);
              return (
              <div key={client.id} className={cn("group px-[calc(var(--tile-pad)*0.8)] py-[calc(var(--tile-pad)*0.7)] transition-opacity", !client.isEnabled && "opacity-60")}>
              <div className="flex items-center gap-3">
                <span title={client.isEnabled ? "Enabled — click to disable" : "Disabled — click to enable"}>
                  <Toggle
                    checked={client.isEnabled}
                    onChange={(v) => void handleToggleClient(client.id, client.name, v)}
                  />
                </span>
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <p className={cn("font-medium", client.isEnabled ? "text-foreground" : "text-muted-foreground")}>{client.name}</p>
                    <Badge variant={healthVariant(client.isEnabled ? client.healthStatus : "disabled")} className="text-[9.5px]">
                      {client.isEnabled ? healthLabel(client.healthStatus) : "Disabled"}
                    </Badge>
                    <span className="rounded-full border border-hairline px-1.5 py-0.5 text-[10px] font-medium uppercase text-muted-foreground">
                      {client.protocol}
                    </span>
                  </div>
                  {/* Movies/TV categories displayed side by side */}
                  <div className="mt-1 flex flex-wrap items-center gap-3">
                    {(client.moviesCategory || client.categoryTemplate) && (
                      <span className="flex items-center gap-1 text-[10.5px]">
                        <Film className="h-3 w-3 text-sky-400" />
                        <span className="font-mono text-sky-400">{client.moviesCategory ?? client.categoryTemplate}</span>
                      </span>
                    )}
                    {client.tvCategory && (
                      <span className="flex items-center gap-1 text-[10.5px]">
                        <Tv className="h-3 w-3 text-violet-400" />
                        <span className="font-mono text-violet-400">{client.tvCategory}</span>
                      </span>
                    )}
                    {client.moviesCategory === client.tvCategory && client.moviesCategory && (
                      <span className="flex items-center gap-1 text-[10px] text-amber-400">
                        <AlertTriangle className="h-3 w-3" />
                        Same category for both — may mix files
                      </span>
                    )}
                    {client.endpointUrl && (
                      <span className="font-mono text-[10.5px] text-muted-foreground">{client.endpointUrl}</span>
                    )}
                    {clientTelemetry && (
                      <span className="font-mono text-[10.5px] text-muted-foreground">
                        {clientTelemetry.summary.activeCount} active · {clientTelemetry.summary.totalSpeedMbps.toFixed(1)} MB/s · {clientTelemetry.summary.importReadyCount} ready to import
                      </span>
                    )}
                  </div>
                  {clientTelemetry ? (
                    <div className="mt-2 flex flex-wrap items-center gap-1.5">
                      {telemetryCapabilityChips(clientTelemetry).map((chip) => (
                        <span
                          key={chip.label}
                          className={cn(
                            "rounded-full border px-2 py-0.5 text-[10px] font-medium",
                            chip.enabled
                              ? "border-primary/25 bg-primary/8 text-primary"
                              : "border-hairline bg-surface-1 text-muted-foreground"
                          )}
                        >
                          {chip.label}
                        </span>
                      ))}
                    </div>
                  ) : null}
                  {clientTelemetry?.lastHealthMessage ? (
                    <p className="mt-1 text-[11px] text-muted-foreground">{clientTelemetry.lastHealthMessage}</p>
                  ) : client.lastHealthMessage ? (
                    <p className="mt-1 text-[11px] text-muted-foreground">{client.lastHealthMessage}</p>
                  ) : null}
                  <div className="mt-2 grid gap-1.5 text-[10.5px] text-muted-foreground sm:grid-cols-3">
                    <HealthFact label="Address" value={client.endpointUrl ?? ([client.host, client.port].filter(Boolean).join(":") || "Not configured")} />
                    <HealthFact label="Last tested" value={client.lastHealthTestUtc ? formatClientHistoryTime(client.lastHealthTestUtc) : "Never"} />
                    <HealthFact label="Response time" value={client.lastHealthLatencyMs != null ? `${client.lastHealthLatencyMs} ms` : "Not yet measured"} />
                    {client.lastHealthFailureCategory ? <HealthFact label="Error" value={client.lastHealthFailureCategory} /> : null}
                  </div>
                  <ClientFileAccess
                    client={client}
                    mappings={pathMappingsByClientId.get(client.id) ?? []}
                    onAdd={handleAddPathMapping}
                    onRemove={handleRemovePathMapping}
                    onTest={handleTestPathMapping}
                  />
                </div>
                <div className="flex items-center gap-2 opacity-0 transition-opacity group-hover:opacity-100">
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => void handleTestClient(client.id)}
                    disabled={busyKey === `test-client:${client.id}`}
                    className="gap-1.5"
                  >
                    {busyKey === `test-client:${client.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : <Wifi className="h-3 w-3" />}
                    Test
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => void handleDeleteClient(client.id, client.name)}
                    disabled={busyKey === `dc:${client.id}`}
                  >
                    {busyKey === `dc:${client.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Trash2 className="h-3.5 w-3.5 text-muted-foreground" />}
                  </Button>
                </div>
              </div>
              {clientTelemetry?.queue.length ? (
                <div className="mt-3 space-y-2 rounded-xl border border-hairline bg-surface-1 p-2">
                  {clientTelemetry.queue.slice(0, 4).map((item) => (
                    <div key={item.id} className="grid gap-2 rounded-lg border border-hairline bg-background/40 px-3 py-2 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-center">
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <p className="truncate text-[12.5px] font-medium text-foreground">{item.title}</p>
                          <Badge variant={item.status === downloadQueueStatuses.stalled ? "destructive" : item.status === downloadQueueStatuses.importReady ? "success" : "default"} className="text-[9px]">
                            {queueStatusLabel(item.status)}
                          </Badge>
                          <span className="font-mono text-[10.5px] text-muted-foreground">{item.progress.toFixed(1)}%</span>
                          <span className="font-mono text-[10.5px] text-muted-foreground">{item.speedMbps.toFixed(1)} MB/s</span>
                        </div>
                        <p className="mt-1 truncate font-mono text-[10.5px] text-muted-foreground">
                          {item.category || "uncategorised"} · {item.releaseName}
                        </p>
                        {importPreviews[item.id] ? (
                          <ImportPreviewPanel preview={importPreviews[item.id]} />
                        ) : null}
                      </div>
                      <div className="flex flex-wrap gap-1.5 lg:justify-end">
                        {item.status === downloadQueueStatuses.importReady ? (
                          <>
                            <Button
                              type="button"
                              size="sm"
                              variant="outline"
                              onClick={() => void handlePreviewImport(item)}
                              disabled={busyKey !== null}
                              className="h-7 px-2 text-[10.5px]"
                            >
                              {busyKey === `import-preview:${item.clientId}:${item.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
                              Preview import
                            </Button>
                            <Button
                              type="button"
                              size="sm"
                              onClick={() => void handleImportNow(item)}
                              disabled={busyKey !== null}
                              className="h-7 px-2 text-[10.5px]"
                            >
                              {busyKey === `import-now:${item.clientId}:${item.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
                              Import now
                            </Button>
                            <Button
                              type="button"
                              size="sm"
                              variant="outline"
                              onClick={() => void handleQueueImport(item)}
                              disabled={busyKey !== null}
                              className="h-7 px-2 text-[10.5px]"
                            >
                              {busyKey === `import-queue:${item.clientId}:${item.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
                              Queue import
                            </Button>
                          </>
                        ) : null}
                        <QueueActionButton busyKey={busyKey} clientId={client.id} item={item} action="pause" onAction={handleQueueAction} />
                        <QueueActionButton busyKey={busyKey} clientId={client.id} item={item} action="resume" onAction={handleQueueAction} />
                        {clientTelemetry?.capabilities.supportsRecheck ? (
                          <QueueActionButton busyKey={busyKey} clientId={client.id} item={item} action="recheck" onAction={handleQueueAction} />
                        ) : null}
                        <Button
                          type="button"
                          size="sm"
                          variant="ghost"
                          onClick={() => setPendingQueueRemoval(item)}
                          disabled={busyKey !== null}
                          className="h-7 px-2 text-[10.5px] text-destructive hover:text-destructive"
                        >
                          Remove
                        </Button>
                      </div>
                    </div>
                  ))}
                </div>
              ) : null}
              {clientTelemetry?.history.length ? (
                <div className="mt-3 rounded-xl border border-hairline bg-surface-1 p-3">
                  <div className="mb-2 flex items-center justify-between gap-3">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">Recent client history</p>
                    <span className="font-mono text-[10.5px] text-muted-foreground">{clientTelemetry.history.length} mapped events</span>
                  </div>
                  <div className="space-y-1.5">
                    {clientTelemetry.history.slice(0, 3).map((item) => (
                      <div key={item.id} className="grid gap-2 rounded-lg border border-hairline bg-background/40 px-3 py-2 md:grid-cols-[minmax(0,1fr)_auto] md:items-center">
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-2">
                            <p className="truncate text-[12px] font-medium text-foreground">{item.title}</p>
                            <Badge variant={item.outcome === "failed" ? "destructive" : item.outcome === downloadQueueStatuses.importReady ? "success" : "default"} className="text-[9px]">
                              {item.outcome}
                            </Badge>
                          </div>
                          <p className="mt-1 truncate font-mono text-[10.5px] text-muted-foreground">
                            {item.indexerName || "unknown indexer"} · {item.category || item.mediaType} · {item.releaseName}
                          </p>
                        </div>
                        <span className="font-mono text-[10.5px] text-muted-foreground">{formatClientHistoryTime(item.completedUtc)}</span>
                      </div>
                    ))}
                  </div>
                </div>
              ) : null}
              </div>
              );
            })}
          </div>
        ) : !showClientAdd && !isRouteLoading && (
          <EmptyState
            size="sm"
            variant="custom"
            title="No download client connected"
            description="Connect qBittorrent, SABnzbd, NZBGet, Deluge, Transmission, or another supported client when you are ready to download approved releases."
            action={
              <Button onClick={() => setShowClientAdd(true)} className="gap-2" size="sm">
                <Plus className="h-4 w-4" />
                Connect downloads
              </Button>
            }
          />
        )}

      </section> : null}

      {/* ── ROUTING section ── */}
      {activeSection === "routing" ? <section className="space-y-[var(--page-gap)]">
        <SectionHeader
          icon={Route}
          title="Library routing"
          meta="Choose the indexers and download clients each library can use. Movie libraries only see movie-compatible indexers."
        />

        {isRouteLoading && libraries.length === 0 ? (
          <RouteSectionSkeleton />
        ) : libraries.length === 0 ? (
          <EmptyState
            size="sm"
            variant="custom"
            title="No libraries yet"
            description="Create a Movies or TV library first, then choose its search and download connections here."
          />
        ) : null}

        {/* Movies libraries */}
        {movieLibraries.length > 0 && (
          <div className="space-y-3">
            <div className="flex items-center gap-2">
              <Film className="h-4 w-4 text-sky-400" />
              <p className="text-[11px] font-bold uppercase tracking-[0.14em] text-sky-400">Movies libraries</p>
            </div>
            {movieLibraries.map((lib) => {
              const libRouting = routing.find((r) => r.libraryId === lib.id) ?? {
                libraryId: lib.id,
                libraryName: lib.name,
                sources: [],
                downloadClients: [],
              };
              return (
                <LibraryRoutingPanel
                  key={lib.id}
                  library={lib}
                  routing={libRouting}
                  indexers={indexers}
                  clients={clients}
                  saving={busyKey === `routing:${lib.id}`}
                  onSave={(src, cli) => handleSaveRouting(lib.id, src, cli)}
                />
              );
            })}
          </div>
        )}

        {/* TV libraries */}
        {tvLibraries.length > 0 && (
          <div className="space-y-3">
            <div className="flex items-center gap-2">
              <Tv className="h-4 w-4 text-violet-400" />
              <p className="text-[11px] font-bold uppercase tracking-[0.14em] text-violet-400">TV libraries</p>
            </div>
            {tvLibraries.map((lib) => {
              const libRouting = routing.find((r) => r.libraryId === lib.id) ?? {
                libraryId: lib.id,
                libraryName: lib.name,
                sources: [],
                downloadClients: [],
              };
              return (
                <LibraryRoutingPanel
                  key={lib.id}
                  library={lib}
                  routing={libRouting}
                  indexers={indexers}
                  clients={clients}
                  saving={busyKey === `routing:${lib.id}`}
                  onSave={(src, cli) => handleSaveRouting(lib.id, src, cli)}
                />
              );
            })}
          </div>
        )}
      </section> : null}

      {/* ── Health watch ── */}
      {activeSection === "overview" && unhealthyCount > 0 && (
        <section className="space-y-3">
          <SectionHeader icon={ShieldAlert} title="Connections needing attention" meta="Indexers or download clients that need a review" />
          <div className="rounded-2xl border border-destructive/20 bg-destructive/5 divide-y divide-destructive/10 overflow-hidden">
            {[...indexers, ...clients]
              .filter((item) => item.isEnabled && item.healthStatus !== "healthy")
              .map((item) => (
                <div key={item.id} className="flex items-start gap-3 px-4 py-3.5">
                  <WifiOff className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
                  <div>
                    <p className="text-[13px] font-medium text-foreground">{item.name}</p>
                    <p className="text-[12px] text-muted-foreground">{item.lastHealthMessage ?? "Needs review."}</p>
                  </div>
                  <Badge variant={healthVariant(item.healthStatus)} className="ml-auto text-[9.5px]">
                    {healthLabel(item.healthStatus)}
                  </Badge>
                </div>
              ))}
          </div>
        </section>
      )}

      <ConfirmDialog
        open={pendingQueueRemoval !== null}
        onOpenChange={(open) => {
          if (!open && busyKey === null) setPendingQueueRemoval(null);
        }}
        title="Remove this download?"
        description={pendingQueueRemoval
          ? `Deluno will ask ${pendingQueueRemoval.clientName} to remove “${pendingQueueRemoval.title}”. This may remove the download-client item and its payload files. It will not remove files already in your media library; cancel if this item could be shared or cross-seeded.`
          : ""}
        confirmLabel="Remove download"
        busy={pendingQueueRemoval !== null && busyKey === `queue:${pendingQueueRemoval.clientId}:${pendingQueueRemoval.id}:delete`}
        onConfirm={() => void confirmQueueRemoval()}
      />
    </div>
  );
}

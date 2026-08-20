/**
 * Connections — indexers, download clients and library routing on the
 * list → drawer grammar. Replaces indexers-screen.tsx.
 *
 *   PageToolbar (Indexers · Download clients · Library routing · New …)
 *   ListCard per tab, one row anatomy, row → drawer.
 *
 * Contracts (unchanged): GET/POST /api/indexers, PUT/DELETE /api/indexers/{id},
 * POST /api/indexers/{id}/test|reset-circuit; GET/POST /api/download-clients,
 * PUT/DELETE /api/download-clients/{id}, POST /api/download-clients/{id}/test,
 * path-mappings CRUD; GET/PUT /api/libraries/{id}/routing; PUT /api/settings;
 * GET /api/integrations/outbound-throttle.
 * Queue actions and import previews live on Transfers.
 */
import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useLoaderData, useLocation, useRevalidator } from "react-router-dom";
import { Loader2, Plus, RefreshCw, Trash2, Wifi } from "lucide-react";
import {
  fetchJson,
  readValidationProblem,
  type DownloadClientItem,
  type DownloadClientPathMappingItem,
  type DownloadClientTelemetrySnapshot,
  type DownloadTelemetryOverview,
  type IndexerItem,
  type LibraryItem,
  type LibraryRoutingSnapshot,
  type OutboundThrottleHostState,
  type OutboundThrottleSnapshot,
  type PlatformSettingsSnapshot
} from "../lib/api";
import { authedFetch } from "../lib/use-auth";
import { useSignalREvent } from "../lib/use-signalr";
import { configurationNavAreas } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Chip, type ChipProps } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerDanger, DrawerFooter, DrawerSection, type DrawerSaveState } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { PresetField } from "../components/ui/preset-field";
import { SegmentedControl } from "../components/ui/segmented-control";
import { Select } from "../components/ui/select";
import { Switch, SwitchRow } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";

/* ============================================================ presets */

type IndexerProtocol = "torznab" | "newznab" | "rss" | "custom";
type MediaScope = "movies" | "tv" | "both";

const INDEXER_PRESETS: { protocol: IndexerProtocol; label: string; hint: string; requiresApiKey: boolean; placeholder: string; defaultCategories: (scope: MediaScope) => string }[] = [
  {
    protocol: "torznab",
    label: "Torznab",
    hint: "Any Torznab-compatible tracker or private site.",
    requiresApiKey: true,
    placeholder: "http://localhost:9117/api/v2.0/indexers/XXX/results/torznab/",
    defaultCategories: (scope) => (scope === "movies" ? "2000,2010,2020,2030,2040,2045,2050,2060,2070" : scope === "tv" ? "5000,5010,5020,5030,5040,5045,5050,5060,5070" : "2000,2010,2020,2030,2040,2045,2050,2060,5000,5010,5020,5030,5040,5045,5050")
  },
  {
    protocol: "newznab",
    label: "Newznab",
    hint: "NZBGeek, DrunkenSlug, NZBCat, or any Newznab-compatible Usenet indexer.",
    requiresApiKey: true,
    placeholder: "https://api.nzbgeek.info",
    defaultCategories: (scope) => (scope === "movies" ? "2000,2010,2020,2030,2040,2045,2050,2060,2070" : scope === "tv" ? "5000,5010,5020,5030,5040,5045,5050,5060,5070" : "2000,2010,2020,2030,2040,2045,2050,2060,5000,5010,5020,5030,5040,5045,5050")
  },
  { protocol: "rss", label: "RSS feed", hint: "Plain RSS feed without authentication.", requiresApiKey: false, placeholder: "https://example.com/feed.rss", defaultCategories: () => "" },
  { protocol: "custom", label: "Custom", hint: "Manual configuration for anything else.", requiresApiKey: false, placeholder: "https://example.com", defaultCategories: () => "" }
];

const CLIENT_PRESETS: { protocol: string; label: string; kind: "Usenet" | "Torrent"; defaultPort: number; defaultMoviesCategory: string; defaultTvCategory: string; authMode: string; setupHint: string }[] = [
  { protocol: "qbittorrent", label: "qBittorrent", kind: "Torrent", defaultPort: 8080, defaultMoviesCategory: "deluno-movies", defaultTvCategory: "deluno-tv", authMode: "Web UI login", setupHint: "Enable the Web UI and use the same username and password you use in qBittorrent." },
  { protocol: "transmission", label: "Transmission", kind: "Torrent", defaultPort: 9091, defaultMoviesCategory: "deluno-movies", defaultTvCategory: "deluno-tv", authMode: "Basic auth", setupHint: "Use the RPC port. Deluno handles the Transmission session token automatically." },
  { protocol: "deluge", label: "Deluge", kind: "Torrent", defaultPort: 8112, defaultMoviesCategory: "deluno-movies", defaultTvCategory: "deluno-tv", authMode: "Password", setupHint: "Use the Deluge Web UI password. Labels are used as Deluno categories." },
  { protocol: "utorrent", label: "uTorrent", kind: "Torrent", defaultPort: 8080, defaultMoviesCategory: "deluno-movies", defaultTvCategory: "deluno-tv", authMode: "Token auth", setupHint: "Use the Web UI credentials. uTorrent may not expose a reliable finished path, so imports can need a file-location link." },
  { protocol: "sabnzbd", label: "SABnzbd", kind: "Usenet", defaultPort: 8080, defaultMoviesCategory: "Movies", defaultTvCategory: "TV", authMode: "API key", setupHint: "Paste the SABnzbd API key into the password field. Categories map directly to SABnzbd folders." },
  { protocol: "nzbget", label: "NZBGet", kind: "Usenet", defaultPort: 6789, defaultMoviesCategory: "Movies", defaultTvCategory: "TV", authMode: "Basic auth", setupHint: "Use the NZBGet username and password." }
];

const PRIORITY_OPTIONS = [
  { label: "Highest (1)", value: "1" },
  { label: "High (5)", value: "5" },
  { label: "Normal (10)", value: "10" },
  { label: "Low (25)", value: "25" },
  { label: "Fallback only (50)", value: "50" }
];
const HOST_OPTIONS = [
  { label: "This machine (localhost)", value: "localhost" },
  { label: "Localhost IPv4 (127.0.0.1)", value: "127.0.0.1" },
  { label: "Docker host (host.docker.internal)", value: "host.docker.internal" }
];
const CATEGORY_OPTIONS = [
  { label: "deluno-movies", value: "deluno-movies" },
  { label: "Movies", value: "Movies" },
  { label: "movies", value: "movies" },
  { label: "radarr", value: "radarr" }
];
const TV_CATEGORY_OPTIONS = [
  { label: "deluno-tv", value: "deluno-tv" },
  { label: "TV", value: "TV" },
  { label: "tv", value: "tv" },
  { label: "sonarr", value: "sonarr" }
];

const PORT_OPTIONS = [...new Map(CLIENT_PRESETS.map((item) => [String(item.defaultPort), item] as const)).entries()].map(([value, item]) => ({
  value,
  label: `${CLIENT_PRESETS.filter((candidate) => candidate.defaultPort === item.defaultPort).map((candidate) => candidate.label).join(" / ")} default (${value})`
}));

const TABS = configurationNavAreas.find((area) => area.label === "Connections")?.items ?? [];

/* ============================================================= loader */

interface LoaderData {
  clients: DownloadClientItem[];
  pathMappings: DownloadClientPathMappingItem[];
  indexers: IndexerItem[];
  libraries: LibraryItem[];
  routing: LibraryRoutingSnapshot[];
  settings: PlatformSettingsSnapshot;
  telemetry: DownloadTelemetryOverview | null;
  outboundThrottle: OutboundThrottleSnapshot;
}

export async function indexersLoader(): Promise<LoaderData> {
  const [indexers, clients, libraries, settings, telemetry, outboundThrottle] = await Promise.all([
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<DownloadClientItem[]>("/api/download-clients"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry").catch(() => null),
    fetchJson<OutboundThrottleSnapshot>("/api/integrations/outbound-throttle").catch(() => ({ hosts: [] }))
  ]);
  const routing = await Promise.all(
    libraries.map((lib) =>
      fetchJson<LibraryRoutingSnapshot>(`/api/libraries/${lib.id}/routing`).catch(() => ({ libraryId: lib.id, libraryName: lib.name, sources: [], downloadClients: [] }))
    )
  );
  const pathMappings = (
    await Promise.all(clients.map((client) => fetchJson<DownloadClientPathMappingItem[]>(`/api/download-clients/${client.id}/path-mappings`).catch(() => [])))
  ).flat();
  return { clients, pathMappings, indexers, libraries, routing, settings, telemetry, outboundThrottle };
}

/* ============================================================ helpers */

function healthChip(item: { isEnabled: boolean; healthStatus: string; rateLimitedUntilUtc?: string | null }): { tone: NonNullable<ChipProps["tone"]>; label: string } {
  if (!item.isEnabled) return { tone: "muted", label: "Off" };
  if (item.rateLimitedUntilUtc && new Date(item.rateLimitedUntilUtc).getTime() > Date.now()) return { tone: "warn", label: "Rate-limited" };
  switch (item.healthStatus) {
    case "healthy":
      return { tone: "ok", label: "Healthy" };
    case "degraded":
      return { tone: "warn", label: "Degraded" };
    case "untested":
      return { tone: "muted", label: "Untested" };
    default:
      return { tone: "bad", label: "Unhealthy" };
  }
}

function relative(iso: string | null | undefined) {
  if (!iso) return "Never";
  const diff = Date.now() - new Date(iso).getTime();
  const minutes = Math.round(Math.abs(diff) / 60000);
  const label = minutes < 1 ? "just now" : minutes < 60 ? `${minutes} min ago` : minutes < 60 * 48 ? `${Math.round(minutes / 60)} h ago` : `${Math.round(minutes / 1440)} d ago`;
  return label;
}

function scopeLabel(scope: string | null | undefined) {
  return scope === "movies" ? "Movies" : scope === "tv" ? "TV" : "Movies · TV";
}

function protocolLabel(protocol: string) {
  return CLIENT_PRESETS.find((preset) => preset.protocol === protocol)?.label ?? INDEXER_PRESETS.find((preset) => preset.protocol === protocol)?.label ?? protocol;
}

function indexerHost(baseUrl: string) {
  try {
    return new URL(baseUrl).hostname.toLowerCase();
  } catch {
    return null;
  }
}

function formatSeconds(seconds: number) {
  return seconds < 1 ? "less than a second" : `${Math.ceil(seconds)} second${seconds >= 1.01 ? "s" : ""}`;
}

async function send(url: string, method: string, body?: unknown, failure = "Request failed.") {
  const response = await authedFetch(url, { method, headers: body === undefined ? undefined : { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok && response.status !== 204) {
    const problem = await readValidationProblem(response.clone()).catch(() => null);
    const detail = problem?.errors ? Object.values(problem.errors).flat()[0] : problem?.title;
    throw new Error(detail || failure);
  }
  return response;
}

/* ============================================================ forms */

interface IndexerForm {
  name: string;
  protocol: IndexerProtocol;
  scope: MediaScope;
  baseUrl: string;
  apiKey: string;
  priority: string;
  requestIntervalSeconds: string;
  categories: string;
  isEnabled: boolean;
}

function emptyIndexerForm(): IndexerForm {
  return { name: "", protocol: "newznab", scope: "both", baseUrl: "", apiKey: "", priority: "10", requestIntervalSeconds: "", categories: INDEXER_PRESETS[1]!.defaultCategories("both"), isEnabled: true };
}
function indexerFormFrom(item: IndexerItem): IndexerForm {
  return {
    name: item.name,
    protocol: (["torznab", "newznab", "rss", "custom"].includes(item.protocol) ? item.protocol : "custom") as IndexerProtocol,
    scope: item.mediaScope ?? "both",
    baseUrl: item.baseUrl,
    apiKey: "",
    priority: String(item.priority),
    requestIntervalSeconds: item.requestIntervalSeconds == null ? "" : String(item.requestIntervalSeconds),
    categories: item.categories,
    isEnabled: item.isEnabled
  };
}
function sameIndexer(a: IndexerForm, b: IndexerForm) {
  return (Object.keys(a) as (keyof IndexerForm)[]).every((key) => a[key] === b[key]);
}

interface ClientForm {
  name: string;
  protocol: string;
  host: string;
  port: string;
  username: string;
  password: string;
  moviesCategory: string;
  tvCategory: string;
  priority: string;
  isEnabled: boolean;
}
function emptyClientForm(): ClientForm {
  const preset = CLIENT_PRESETS[0]!;
  return { name: "", protocol: preset.protocol, host: "localhost", port: String(preset.defaultPort), username: "", password: "", moviesCategory: preset.defaultMoviesCategory, tvCategory: preset.defaultTvCategory, priority: "1", isEnabled: true };
}
function clientFormFrom(item: DownloadClientItem): ClientForm {
  return {
    name: item.name,
    protocol: item.protocol,
    host: item.host ?? "",
    port: item.port ? String(item.port) : "",
    username: item.username ?? "",
    password: "",
    moviesCategory: item.moviesCategory ?? item.categoryTemplate ?? "",
    tvCategory: item.tvCategory ?? "",
    priority: String(item.priority),
    isEnabled: item.isEnabled
  };
}
function sameClient(a: ClientForm, b: ClientForm) {
  return (Object.keys(a) as (keyof ClientForm)[]).every((key) => a[key] === b[key]);
}

type Section = "indexers" | "clients" | "routing";
type DrawerState =
  | { kind: "closed" }
  | { kind: "indexer"; id: string | null }
  | { kind: "client"; id: string | null }
  | { kind: "routing"; libraryId: string };

/* ============================================================== page */

export function IndexersPage() {
  const { clients, pathMappings, indexers, libraries, routing, settings, telemetry, outboundThrottle } = useLoaderData() as LoaderData;
  const location = useLocation();
  const revalidator = useRevalidator();
  const lastTelemetryRefresh = useRef(0);
  const [liveOutboundThrottle, setLiveOutboundThrottle] = useState(outboundThrottle);

  const section: Section = location.pathname.endsWith("/download-clients") ? "clients" : location.pathname.endsWith("/library-routing") ? "routing" : "indexers";

  useEffect(() => {
    setLiveOutboundThrottle(outboundThrottle);
  }, [outboundThrottle]);

  useEffect(() => {
    if (section !== "indexers") return;

    let mounted = true;
    const refresh = () => {
      void fetchJson<OutboundThrottleSnapshot>("/api/integrations/outbound-throttle")
        .then((snapshot) => {
          if (mounted) setLiveOutboundThrottle(snapshot);
        })
        .catch(() => undefined);
    };
    const interval = window.setInterval(refresh, 5000);
    return () => {
      mounted = false;
      window.clearInterval(interval);
    };
  }, [section]);

  useSignalREvent("DownloadProgress", () => {
    const now = Date.now();
    if (revalidator.state === "idle" && now - lastTelemetryRefresh.current > 5000) {
      lastTelemetryRefresh.current = now;
      revalidator.revalidate();
    }
  });

  const telemetryByClient = useMemo(() => new Map<string, DownloadClientTelemetrySnapshot>(telemetry?.clients.map((item) => [item.clientId, item]) ?? []), [telemetry]);
  const throttleByHost = useMemo(() => new Map<string, OutboundThrottleHostState>(liveOutboundThrottle.hosts.map((item) => [item.host.toLowerCase(), item])), [liveOutboundThrottle]);
  const mappingsByClient = useMemo(() => {
    const map = new Map<string, DownloadClientPathMappingItem[]>();
    for (const mapping of pathMappings) map.set(mapping.downloadClientId, [...(map.get(mapping.downloadClientId) ?? []), mapping]);
    return map;
  }, [pathMappings]);

  /* ---------------------------------------------------------- drawer */
  const [drawer, setDrawer] = useState<DrawerState>({ kind: "closed" });
  const [indexerForm, setIndexerForm] = useState<IndexerForm>(emptyIndexerForm);
  const [indexerInitial, setIndexerInitial] = useState<IndexerForm>(emptyIndexerForm);
  const [clientForm, setClientForm] = useState<ClientForm>(emptyClientForm);
  const [clientInitial, setClientInitial] = useState<ClientForm>(emptyClientForm);
  const [routeSources, setRouteSources] = useState<string[]>([]);
  const [routeClients, setRouteClients] = useState<string[]>([]);
  const [routeInitial, setRouteInitial] = useState<{ sources: string[]; clients: string[] }>({ sources: [], clients: [] });
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [showKey, setShowKey] = useState(false);
  const [fineTuneOpen, setFineTuneOpen] = useState(false);
  const [newMapping, setNewMapping] = useState({ remotePath: "", localPath: "" });

  const editingIndexer = drawer.kind === "indexer" && drawer.id ? indexers.find((item) => item.id === drawer.id) ?? null : null;
  const editingIndexerThrottle = editingIndexer ? throttleByHost.get(indexerHost(editingIndexer.baseUrl) ?? "") ?? null : null;
  const editingClient = drawer.kind === "client" && drawer.id ? clients.find((item) => item.id === drawer.id) ?? null : null;
  const routingLibrary = drawer.kind === "routing" ? libraries.find((item) => item.id === drawer.libraryId) ?? null : null;

  const dirty = useMemo(() => {
    if (drawer.kind === "indexer") return !sameIndexer(indexerForm, indexerInitial);
    if (drawer.kind === "client") return !sameClient(clientForm, clientInitial);
    if (drawer.kind === "routing") return !sameSet(routeSources, routeInitial.sources) || !sameSet(routeClients, routeInitial.clients);
    return false;
  }, [drawer.kind, indexerForm, indexerInitial, clientForm, clientInitial, routeSources, routeClients, routeInitial]);
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  const blocker = useUnsavedChanges(dirty);

  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  function resetDrawerChrome() {
    setSaveState(undefined);
    setFieldErrors({});
    setShowKey(false);
    setFineTuneOpen(false);
    setNewMapping({ remotePath: "", localPath: "" });
  }
  function openIndexer(item: IndexerItem | null) {
    const next = item ? indexerFormFrom(item) : emptyIndexerForm();
    setDrawer({ kind: "indexer", id: item?.id ?? null });
    setIndexerForm(next);
    setIndexerInitial(next);
    resetDrawerChrome();
  }
  function openClient(item: DownloadClientItem | null) {
    const next = item ? clientFormFrom(item) : emptyClientForm();
    setDrawer({ kind: "client", id: item?.id ?? null });
    setClientForm(next);
    setClientInitial(next);
    resetDrawerChrome();
  }
  function openRouting(library: LibraryItem) {
    const snapshot = routing.find((item) => item.libraryId === library.id);
    const sources = snapshot?.sources.map((source) => source.indexerId) ?? [];
    const clientIds = snapshot?.downloadClients.map((client) => client.downloadClientId) ?? [];
    setDrawer({ kind: "routing", libraryId: library.id });
    setRouteSources(sources);
    setRouteClients(clientIds);
    setRouteInitial({ sources, clients: clientIds });
    resetDrawerChrome();
  }
  function closeDrawer() {
    setDrawer({ kind: "closed" });
    setConfirmDiscard(false);
  }
  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  /* ---------------------------------------------------------- saving */
  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    const errors: Record<string, string> = {};
    if (drawer.kind === "indexer") {
      if (!indexerForm.name.trim()) errors.name = "Give this indexer a name.";
      if (!indexerForm.baseUrl.trim()) errors.baseUrl = "Enter the indexer URL.";
      const preset = INDEXER_PRESETS.find((item) => item.protocol === indexerForm.protocol);
      if (!drawer.id && preset?.requiresApiKey && !indexerForm.apiKey.trim()) errors.apiKey = "This indexer needs an API key.";
    } else if (drawer.kind === "client") {
      if (!clientForm.name.trim()) errors.name = "Give this client a name.";
      if (!clientForm.host.trim()) errors.host = "Enter the host or IP.";
      if (!clientForm.port.trim() || Number.isNaN(Number(clientForm.port))) errors.port = "Enter a port number.";
    }
    setFieldErrors(errors);
    if (Object.keys(errors).length) return;

    setBusy("save");
    setSaveState("saving");
    try {
      if (drawer.kind === "indexer") {
        const payload = {
          name: indexerForm.name.trim(),
          protocol: indexerForm.protocol,
          privacy: "private",
          baseUrl: indexerForm.baseUrl.trim(),
          apiKey: indexerForm.apiKey.trim() || undefined,
          priority: Number(indexerForm.priority || 10),
          requestIntervalSeconds: indexerForm.requestIntervalSeconds.trim() ? Number(indexerForm.requestIntervalSeconds) : null,
          clearRequestInterval: Boolean(drawer.id) && !indexerForm.requestIntervalSeconds.trim(),
          categories: indexerForm.categories,
          tags: "",
          mediaScope: indexerForm.scope,
          isEnabled: indexerForm.isEnabled
        };
        if (drawer.id) {
          await send(`/api/indexers/${drawer.id}`, "PUT", payload, "Indexer could not be saved.");
          const settled = { ...indexerForm, apiKey: "" };
          setIndexerForm(settled);
          setIndexerInitial(settled);
          setSaveMessage("Saved just now");
        } else {
          const response = await send("/api/indexers", "POST", payload, "Indexer could not be added.");
          const created = (await response.json()) as IndexerItem;
          const settled = indexerFormFrom(created);
          setIndexerForm(settled);
          setIndexerInitial(settled);
          setDrawer({ kind: "indexer", id: created.id });
          setSaveMessage("Indexer added");
        }
      } else if (drawer.kind === "client") {
        const preset = CLIENT_PRESETS.find((item) => item.protocol === clientForm.protocol);
        const port = Number(clientForm.port || preset?.defaultPort || 8080);
        const payload = {
          name: clientForm.name.trim(),
          protocol: clientForm.protocol,
          host: clientForm.host.trim(),
          port,
          username: clientForm.username || undefined,
          password: clientForm.password || undefined,
          endpointUrl: `http://${clientForm.host.trim()}:${port}`,
          moviesCategory: clientForm.moviesCategory,
          tvCategory: clientForm.tvCategory,
          categoryTemplate: clientForm.moviesCategory,
          priority: Number(clientForm.priority || 1),
          isEnabled: clientForm.isEnabled
        };
        if (drawer.id) {
          await send(`/api/download-clients/${drawer.id}`, "PUT", payload, "Download client could not be saved.");
          const settled = { ...clientForm, password: "" };
          setClientForm(settled);
          setClientInitial(settled);
          setSaveMessage("Saved just now");
        } else {
          const response = await send("/api/download-clients", "POST", payload, "Download client could not be added.");
          const created = (await response.json()) as DownloadClientItem;
          const settled = clientFormFrom(created);
          setClientForm(settled);
          setClientInitial(settled);
          setDrawer({ kind: "client", id: created.id });
          setSaveMessage("Client added");
        }
      } else if (drawer.kind === "routing") {
        await send(
          `/api/libraries/${drawer.libraryId}/routing`,
          "PUT",
          {
            sources: routeSources.map((indexerId, index) => ({ indexerId, priority: index + 1, requiredTags: "", excludedTags: "" })),
            downloadClients: routeClients.map((downloadClientId, index) => ({ downloadClientId, priority: index + 1 }))
          },
          "Routing could not be saved."
        );
        setRouteInitial({ sources: routeSources, clients: routeClients });
        setSaveMessage("Routing saved");
      }
      setSaveState("saved");
      revalidator.revalidate();
    } catch (error) {
      setSaveState("error");
      setSaveMessage(error instanceof Error ? error.message : "Could not save");
    } finally {
      setBusy(null);
    }
  }

  async function handleRemove() {
    if (drawer.kind === "indexer" && drawer.id) {
      await run("remove", () => send(`/api/indexers/${drawer.id}`, "DELETE", undefined, "Indexer could not be removed."), `${editingIndexer?.name ?? "Indexer"} removed`);
    } else if (drawer.kind === "client" && drawer.id) {
      await run("remove", () => send(`/api/download-clients/${drawer.id}`, "DELETE", undefined, "Download client could not be removed."), `${editingClient?.name ?? "Client"} removed`);
    } else {
      return;
    }
    setConfirmRemove(false);
    setIndexerInitial(indexerForm);
    setClientInitial(clientForm);
    closeDrawer();
  }

  async function run(key: string, action: () => Promise<unknown>, success?: string) {
    setBusy(key);
    try {
      await action();
      if (success) toast.success(success);
      revalidator.revalidate();
      return true;
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Action failed.");
      return false;
    } finally {
      setBusy(null);
    }
  }

  // Test results land in the drawer's Health section after revalidation; no toast needed.
  async function testIndexer(id: string) {
    await run(`test:${id}`, () => send(`/api/indexers/${id}/test`, "POST", undefined, "Test failed."));
  }
  async function testClient(id: string) {
    await run(`test:${id}`, () => send(`/api/download-clients/${id}/test`, "POST", undefined, "Test failed."));
  }
  async function toggleIndexer(item: IndexerItem, isEnabled: boolean) {
    await run(`toggle:${item.id}`, () => send(`/api/indexers/${item.id}`, "PUT", { isEnabled }, `Could not ${isEnabled ? "enable" : "pause"} ${item.name}.`));
    if (drawer.kind === "indexer" && drawer.id === item.id && !dirty) {
      const next = { ...indexerForm, isEnabled };
      setIndexerForm(next);
      setIndexerInitial(next);
    }
  }
  async function toggleClient(item: DownloadClientItem, isEnabled: boolean) {
    await run(`toggle:${item.id}`, () => send(`/api/download-clients/${item.id}`, "PUT", { isEnabled }, `Could not ${isEnabled ? "enable" : "pause"} ${item.name}.`));
    if (drawer.kind === "client" && drawer.id === item.id && !dirty) {
      const next = { ...clientForm, isEnabled };
      setClientForm(next);
      setClientInitial(next);
    }
  }
  async function setRemovalPermission(allowed: boolean) {
    await run("permission", () => send("/api/settings", "PUT", { ...settings, removeCompletedDownloads: allowed }, "Setting could not be saved."));
  }
  async function addMapping(clientId: string) {
    if (!newMapping.remotePath.trim() || !newMapping.localPath.trim()) return;
    const ok = await run("mapping:add", () => send(`/api/download-clients/${clientId}/path-mappings`, "POST", { remotePath: newMapping.remotePath.trim(), localPath: newMapping.localPath.trim(), isEnabled: true, priority: 10 }, "File location link could not be saved."), "File location linked");
    if (ok) setNewMapping({ remotePath: "", localPath: "" });
  }
  async function testMapping(clientId: string, mappingId: string) {
    await run(`mapping:test:${mappingId}`, async () => {
      const response = await send(`/api/download-clients/${clientId}/path-mappings/${mappingId}/test`, "POST", undefined, "Deluno could not test this file location.");
      const result = (await response.json()) as { reachable: boolean; message: string };
      if (result.reachable) toast.success(result.message);
      else toast.error(result.message);
    });
  }

  /* ---------------------------------------------------------- render */
  const enabledIndexers = indexers.filter((item) => item.isEnabled).length;
  const healthyIndexers = indexers.filter((item) => item.isEnabled && item.healthStatus === "healthy").length;

  const toolbarAction =
    section === "indexers" ? (
      <Button type="button" onClick={() => openIndexer(null)}>
        <Plus className="h-4 w-4" />
        New indexer
      </Button>
    ) : section === "clients" ? (
      <Button type="button" onClick={() => openClient(null)}>
        <Plus className="h-4 w-4" />
        New client
      </Button>
    ) : null;

  return (
    <div className="grid gap-[var(--page-gap)]">
      <PageToolbar tabs={TABS} actions={toolbarAction} />

      {section === "indexers" ? (
        <ListCard title="Indexers" count={indexers.length ? `${indexers.length} ${indexers.length === 1 ? "indexer" : "indexers"} · ${healthyIndexers}/${enabledIndexers} healthy` : undefined}>
          {indexers.length === 0 ? (
            <ListEmpty
              title="No indexers yet"
              description="Add a Torznab, Newznab or RSS indexer when you're ready for Deluno to start looking for releases."
              actions={
                <Button type="button" size="sm" onClick={() => openIndexer(null)}>
                  <Plus className="h-3.5 w-3.5" />
                  New indexer
                </Button>
              }
            />
          ) : (
            <ListTable columns={[{ label: "Name" }, { label: "Protocol" }, { label: "Used for" }, { label: "Last test" }, { label: "Pacing" }, { label: "Status", width: LIST_TRACK.status, mobile: true }, { label: "On", width: LIST_TRACK.toggle, mobile: true }]}>
              {indexers.map((item) => {
                const chip = healthChip(item);
                const throttle = throttleByHost.get(indexerHost(item.baseUrl) ?? "");
                return (
                  <ListRow key={item.id} onClick={() => openIndexer(item)} selected={drawer.kind === "indexer" && drawer.id === item.id}>
                    <ListNameCell name={item.name} sub={item.baseUrl} />
                    <ListCell primary={protocolLabel(item.protocol)} secondary={item.privacy === "private" ? "Private" : "Public"} />
                    <ListCell primary={scopeLabel(item.mediaScope)} secondary={`Priority ${item.priority}`} />
                    <ListCell numeric primary={relative(item.lastHealthTestUtc)} secondary={item.consecutiveFailures > 0 ? `${item.consecutiveFailures} consecutive failure${item.consecutiveFailures === 1 ? "" : "s"}` : item.lastHealthLatencyMs != null ? `${item.lastHealthLatencyMs} ms` : item.lastHealthMessage ?? "—"} />
                    <ListCell primary={<span aria-live="polite">{throttle?.waiting ? `${throttle.waiting} request${throttle.waiting === 1 ? "" : "s"} waiting` : throttle?.nextPermitInSeconds ? `Next in ${formatSeconds(throttle.nextPermitInSeconds)}` : "Ready"}</span>} secondary={throttle ? `Deluno is pacing ${throttle.host}` : "No recent requests"} />
                    <ListCell mobile>
                      <Chip tone={chip.tone}>{chip.label}</Chip>
                    </ListCell>
                    <ListCell mobile>
                      <Switch size="sm" aria-label={`${item.isEnabled ? "Pause" : "Enable"} ${item.name}`} checked={item.isEnabled} disabled={busy === `toggle:${item.id}`} onCheckedChange={(checked) => void toggleIndexer(item, checked)} />
                    </ListCell>
                  </ListRow>
                );
              })}
            </ListTable>
          )}
        </ListCard>
      ) : null}

      {section === "clients" ? (
        <>
          <ListCard title="Download clients" count={clients.length ? `${clients.length} ${clients.length === 1 ? "client" : "clients"}` : undefined}>
            {clients.length === 0 ? (
              <ListEmpty
                title="No download client yet"
                description="Connect qBittorrent, SABnzbd, NZBGet, Deluge, Transmission or uTorrent when you're ready to download approved releases."
                actions={
                  <Button type="button" size="sm" onClick={() => openClient(null)}>
                    <Plus className="h-3.5 w-3.5" />
                    New client
                  </Button>
                }
              />
            ) : (
              <ListTable columns={[{ label: "Name" }, { label: "Type" }, { label: "Categories" }, { label: "Now" }, { label: "Status", width: LIST_TRACK.status, mobile: true }, { label: "On", width: LIST_TRACK.toggle, mobile: true }]}>
                {clients.map((item) => {
                  const chip = healthChip(item);
                  const preset = CLIENT_PRESETS.find((candidate) => candidate.protocol === item.protocol);
                  const live = telemetryByClient.get(item.id);
                  const movies = item.moviesCategory ?? item.categoryTemplate ?? "";
                  const tv = item.tvCategory ?? "";
                  const mappings = mappingsByClient.get(item.id) ?? [];
                  return (
                    <ListRow key={item.id} onClick={() => openClient(item)} selected={drawer.kind === "client" && drawer.id === item.id}>
                      <ListNameCell name={item.name} sub={item.endpointUrl ?? [item.host, item.port].filter(Boolean).join(":")} />
                      <ListCell primary={preset?.label ?? item.protocol} secondary={preset ? `${preset.kind} · ${preset.authMode}` : undefined} />
                      <ListCell mono primary={[movies, tv].filter(Boolean).join(" · ") || "—"} secondary={movies && tv && movies === tv ? "Same for both — files will mix" : mappings.length ? `${mappings.length} file-location link${mappings.length === 1 ? "" : "s"}` : "Movies · TV"} />
                      <ListCell numeric primary={live ? `${live.summary.activeCount} active` : <span className="text-muted-foreground">—</span>} secondary={live ? `${live.summary.totalSpeedMbps.toFixed(1)} MB/s · ${live.summary.importReadyCount} ready to import` : item.lastHealthTestUtc ? `Tested ${relative(item.lastHealthTestUtc)}` : "Not tested"} />
                      <ListCell mobile>
                        <Chip tone={chip.tone}>{chip.label}</Chip>
                      </ListCell>
                      <ListCell mobile>
                        <Switch size="sm" aria-label={`${item.isEnabled ? "Pause" : "Enable"} ${item.name}`} checked={item.isEnabled} disabled={busy === `toggle:${item.id}`} onCheckedChange={(checked) => void toggleClient(item, checked)} />
                      </ListCell>
                    </ListRow>
                  );
                })}
              </ListTable>
            )}
          </ListCard>

          <ListCard title="Permissions">
            <ListTable columns={[{ label: "Permission" }, { label: "Applies to" }, { label: "Status", width: LIST_TRACK.status, mobile: true }, { label: "On", width: LIST_TRACK.toggle, mobile: true }]} chevron={false}>
              <ListRow>
                <ListNameCell name="Remove items from the client queue" sub="A confirmed, manual Remove on Transfers. Automatic cleanup is configured in Automation & recovery." />
                <ListCell primary="All download clients" secondary="SABnzbd, qBittorrent, …" />
                <ListCell mobile>
                  <Chip tone={settings.removeCompletedDownloads ? "ok" : "muted"}>{settings.removeCompletedDownloads ? "Allowed" : "Blocked"}</Chip>
                </ListCell>
                <ListCell mobile>
                  <Switch size="sm" aria-label="Allow removing client queue entries" checked={settings.removeCompletedDownloads} disabled={busy === "permission"} onCheckedChange={(checked) => void setRemovalPermission(checked)} />
                </ListCell>
              </ListRow>
            </ListTable>
          </ListCard>
        </>
      ) : null}

      {section === "routing" ? (
        <ListCard title="Library routing" count={libraries.length ? `${libraries.length} ${libraries.length === 1 ? "library" : "libraries"}` : undefined}>
          {libraries.length === 0 ? (
            <ListEmpty title="No libraries yet" description="Create a Movies or TV library first, then choose its search and download connections here." />
          ) : (
            <ListTable columns={[{ label: "Name" }, { label: "Indexers" }, { label: "Download clients" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
              {libraries.map((library) => {
                const snapshot = routing.find((item) => item.libraryId === library.id);
                const sources = snapshot?.sources ?? [];
                const targets = snapshot?.downloadClients ?? [];
                const status = !sources.length ? { tone: "warn" as const, label: "No indexers" } : !targets.length ? { tone: "warn" as const, label: "No client" } : { tone: "ok" as const, label: "Ready" };
                return (
                  <ListRow key={library.id} onClick={() => openRouting(library)} selected={drawer.kind === "routing" && drawer.libraryId === library.id}>
                    <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                    <ListCell numeric primary={sources.length ? `${sources.length} of ${indexers.length}` : <span className="text-muted-foreground">None</span>} secondary={sources.map((source) => source.indexerName).join(", ") || "Deluno can't search for this library"} />
                    <ListCell numeric primary={targets.length ? `${targets.length} of ${clients.length}` : <span className="text-muted-foreground">None</span>} secondary={targets.map((target) => target.downloadClientName).join(", ") || "Nowhere to send releases"} />
                    <ListCell mobile>
                      <Chip tone={status.tone}>{status.label}</Chip>
                    </ListCell>
                  </ListRow>
                );
              })}
            </ListTable>
          )}
        </ListCard>
      ) : null}

      {/* ---------------------------------------------------- drawers */}
      <Drawer
        open={drawer.kind !== "closed"}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={
          drawer.kind === "indexer" ? editingIndexer?.name ?? (indexerForm.name || "New indexer")
          : drawer.kind === "client" ? editingClient?.name ?? (clientForm.name || "New download client")
          : drawer.kind === "routing" ? routingLibrary?.name ?? "Library routing"
          : ""
        }
        description={
          drawer.kind === "indexer" ? (editingIndexer ? `${protocolLabel(editingIndexer.protocol)} indexer · used for ${scopeLabel(editingIndexer.mediaScope)}` : "Where Deluno looks for releases.")
          : drawer.kind === "client" ? (editingClient ? `${protocolLabel(editingClient.protocol)} · ${editingClient.endpointUrl ?? ""}` : "Where Deluno sends approved releases.")
          : drawer.kind === "routing" ? `Which connections ${routingLibrary?.name ?? "this library"} may use`
          : undefined
        }
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter
            state={footerState}
            message={saveMessage}
            saveLabel={drawer.kind === "routing" ? "Save routing" : drawer.kind === "client" ? (drawer.id ? "Save client" : "Add client") : drawer.kind === "indexer" && drawer.id ? "Save indexer" : "Add indexer"}
            onCancel={requestClose}
            disabled={busy !== null}
          />
        }
      >
        {drawer.kind === "indexer" ? (
          <IndexerDrawerBody
            form={indexerForm}
            setForm={setIndexerForm}
            editing={editingIndexer}
            throttle={editingIndexerThrottle}
            errors={fieldErrors}
            clearError={(key) => setFieldErrors((current) => ({ ...current, [key]: "" }))}
            showKey={showKey}
            setShowKey={setShowKey}
            fineTuneOpen={fineTuneOpen}
            setFineTuneOpen={setFineTuneOpen}
            busy={busy}
            onTest={() => editingIndexer && void testIndexer(editingIndexer.id)}
            onReset={() => editingIndexer && void run(`reset:${editingIndexer.id}`, () => send(`/api/indexers/${editingIndexer.id}/reset-circuit`, "POST", undefined, "Could not reset."), `Circuit reset for ${editingIndexer.name}`)}
            onRemove={() => setConfirmRemove(true)}
          />
        ) : null}
        {drawer.kind === "client" ? (
          <ClientDrawerBody
            form={clientForm}
            setForm={setClientForm}
            editing={editingClient}
            errors={fieldErrors}
            clearError={(key) => setFieldErrors((current) => ({ ...current, [key]: "" }))}
            mappings={editingClient ? mappingsByClient.get(editingClient.id) ?? [] : []}
            newMapping={newMapping}
            setNewMapping={setNewMapping}
            busy={busy}
            onAddMapping={() => editingClient && void addMapping(editingClient.id)}
            onRemoveMapping={(mappingId) => editingClient && void run(`mapping:remove:${mappingId}`, () => send(`/api/download-clients/${editingClient.id}/path-mappings/${mappingId}`, "DELETE", undefined, "File location link could not be removed."), "File location link removed")}
            onTestMapping={(mappingId) => editingClient && void testMapping(editingClient.id, mappingId)}
            onTest={() => editingClient && void testClient(editingClient.id)}
            onRemove={() => setConfirmRemove(true)}
          />
        ) : null}
        {drawer.kind === "routing" && routingLibrary ? (
          <RoutingDrawerBody
            library={routingLibrary}
            indexers={indexers}
            clients={clients}
            sources={routeSources}
            targets={routeClients}
            onToggleSource={(id, on) => setRouteSources((current) => (on ? [...new Set([...current, id])] : current.filter((item) => item !== id)))}
            onToggleClient={(id, on) => setRouteClients((current) => (on ? [...new Set([...current, id])] : current.filter((item) => item !== id)))}
          />
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Remove “${drawer.kind === "indexer" ? editingIndexer?.name : editingClient?.name}”?`}
        description={drawer.kind === "indexer" ? "Libraries routed only to this indexer will need another one before Deluno can search for them." : "Downloads already in this client are left alone; Deluno just stops sending new ones there."}
        confirmLabel={drawer.kind === "indexer" ? "Remove indexer" : "Remove client"}
        busy={busy === "remove"}
        onConfirm={() => void handleRemove()}
      />
      <ConfirmDialog
        open={confirmDiscard || blocker.state === "blocked"}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
          if (blocker.state === "blocked") blocker.reset();
        }}
        title="Discard unsaved changes?"
        description="Your edits haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          if (blocker.state === "blocked") {
            setDrawer({ kind: "closed" });
            blocker.proceed();
          } else {
            closeDrawer();
          }
        }}
      />
    </div>
  );
}

/* ====================================================== drawer bodies */

function IndexerDrawerBody({
  form,
  setForm,
  editing,
  throttle,
  errors,
  clearError,
  showKey,
  setShowKey,
  fineTuneOpen,
  setFineTuneOpen,
  busy,
  onTest,
  onReset,
  onRemove
}: {
  form: IndexerForm;
  setForm: (updater: (current: IndexerForm) => IndexerForm) => void;
  editing: IndexerItem | null;
  throttle: OutboundThrottleHostState | null;
  errors: Record<string, string>;
  clearError: (key: string) => void;
  showKey: boolean;
  setShowKey: (value: boolean) => void;
  fineTuneOpen: boolean;
  setFineTuneOpen: (value: boolean) => void;
  busy: string | null;
  onTest: () => void;
  onReset: () => void;
  onRemove: () => void;
}) {
  const preset = INDEXER_PRESETS.find((item) => item.protocol === form.protocol)!;
  const chip = editing ? healthChip(editing) : null;

  function chooseProtocol(protocol: IndexerProtocol) {
    const next = INDEXER_PRESETS.find((item) => item.protocol === protocol)!;
    setForm((current) => ({ ...current, protocol, categories: next.defaultCategories(current.scope) }));
  }
  function chooseScope(scope: MediaScope) {
    setForm((current) => ({ ...current, scope, categories: preset.defaultCategories(scope) || current.categories }));
  }

  return (
    <>
      <DrawerSection title="Basics">
        <FieldRow>
          <Field label="Name" error={errors.name}>
            <Input value={form.name} onChange={(event) => { clearError("name"); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="e.g. NZBgeek" autoComplete="off" />
          </Field>
          <Field label="Protocol" help={editing ? undefined : preset.hint}>
            <Select value={form.protocol} disabled={Boolean(editing)} onChange={(event) => chooseProtocol(event.target.value as IndexerProtocol)} options={INDEXER_PRESETS.map((item) => ({ value: item.protocol, label: item.label }))} />
          </Field>
        </FieldRow>
        <Field label={form.protocol === "torznab" ? "Torznab URL" : form.protocol === "newznab" ? "Newznab URL" : "URL"} error={errors.baseUrl}>
          <Input value={form.baseUrl} onChange={(event) => { clearError("baseUrl"); setForm((current) => ({ ...current, baseUrl: event.target.value })); }} placeholder={preset.placeholder} className="font-mono text-[length:var(--type-caption)]" autoComplete="off" spellCheck={false} />
        </Field>
        {preset.requiresApiKey || form.apiKey ? (
          <Field label="API key" error={errors.apiKey} help={editing ? "Stored encrypted. Leave blank to keep the current key; paste a new one to rotate it." : "From your indexer's account or settings page."}>
            <span className="relative block">
              <Input type={showKey ? "text" : "password"} value={form.apiKey} onChange={(event) => { clearError("apiKey"); setForm((current) => ({ ...current, apiKey: event.target.value })); }} placeholder={editing ? "••••••••••••  (unchanged)" : "Paste your API key"} className="pr-16 font-mono" autoComplete="off" spellCheck={false} />
              <button type="button" onClick={() => setShowKey(!showKey)} className="absolute right-3 top-1/2 -translate-y-1/2 text-[length:var(--type-caption)] font-medium text-primary hover:underline">
                {showKey ? "Hide" : "Reveal"}
              </button>
            </span>
          </Field>
        ) : null}
        <FieldRow>
          <Field label="Used for">
            <SegmentedControl<MediaScope> value={form.scope} onValueChange={chooseScope} options={[{ value: "both", label: "Both" }, { value: "movies", label: "Movies" }, { value: "tv", label: "TV" }]} />
          </Field>
          <Field label="Priority" help="Lower numbers are tried first.">
            <PresetField inputType="number" value={form.priority} onChange={(value) => setForm((current) => ({ ...current, priority: value }))} options={PRIORITY_OPTIONS} customLabel="Custom priority" customPlaceholder="1–50" />
          </Field>
        </FieldRow>
        <SwitchRow label="Enabled" description="Included in automatic and interactive searches." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
      </DrawerSection>

      <DrawerSection title="Categories" aside={form.categories ? `${form.categories.split(",").filter(Boolean).length} selected` : undefined}>
        <Field label="Category ids" help="Filled from the protocol and media type. Change only if your indexer uses non-standard ids.">
          <Input value={form.categories} onChange={(event) => setForm((current) => ({ ...current, categories: event.target.value }))} className="font-mono text-[length:var(--type-caption)]" placeholder="2000,2040,5000,5040" />
        </Field>
        <Disclosure title="Fine-tune" summary="Use Deluno's safe 2-second default, or follow this indexer's published limit." open={fineTuneOpen} onOpenChange={setFineTuneOpen}>
          <Field label="Request interval" error={errors.requestIntervalSeconds} help="Deluno will not query this indexer more often than this. Private trackers usually publish a limit; leave this alone if you are not sure.">
            <div className="flex items-center gap-2">
              <Input type="number" min="2" max="60" step="1" value={form.requestIntervalSeconds} onChange={(event) => { clearError("requestIntervalSeconds"); setForm((current) => ({ ...current, requestIntervalSeconds: event.target.value })); }} placeholder="Deluno default (2 seconds)" />
              <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">seconds</span>
            </div>
          </Field>
          <p className="text-[length:var(--type-caption)] text-muted-foreground">Custom intervals must be between 2 and 60 seconds. The default is 2 seconds.</p>
        </Disclosure>
      </DrawerSection>

      {editing && chip ? (
        <DrawerSection title="Health">
          <dl className="grid grid-cols-[120px_1fr] items-center gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
            <dt className="text-muted-foreground">Last test</dt>
            <dd className="flex items-center gap-2"><Chip tone={chip.tone}>{chip.label}</Chip><span className="text-muted-foreground">{relative(editing.lastHealthTestUtc)}{editing.lastHealthLatencyMs != null ? ` · ${editing.lastHealthLatencyMs} ms` : ""}</span></dd>
            <dt className="text-muted-foreground">Pacing</dt>
            <dd aria-live="polite" className="text-foreground">{throttle?.waiting ? `Deluno is waiting on ${throttle.host} before sending ${throttle.waiting} request${throttle.waiting === 1 ? "" : "s"}.` : throttle?.nextPermitInSeconds ? `Deluno will send the next request to ${throttle.host} in about ${formatSeconds(throttle.nextPermitInSeconds)}.` : "No request is waiting. Deluno will still follow this indexer's safe request interval."}</dd>
            {editing.lastHealthMessage ? (<><dt className="text-muted-foreground">Message</dt><dd className="text-foreground">{editing.lastHealthMessage}</dd></>) : null}
            {editing.consecutiveFailures > 0 ? (<><dt className="text-muted-foreground">Failures</dt><dd className="text-warning">{editing.consecutiveFailures} in a row{editing.disabledReason ? ` — ${editing.disabledReason}` : ""}</dd></>) : null}
          </dl>
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" size="sm" onClick={onTest} disabled={busy !== null || !editing.isEnabled}>
              {busy === `test:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wifi className="h-3.5 w-3.5" />}
              Test connection
            </Button>
            {editing.consecutiveFailures > 0 ? (
              <Button type="button" variant="outline" size="sm" onClick={onReset} disabled={busy !== null}>
                {busy === `reset:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
                Reset failures
              </Button>
            ) : null}
          </div>
        </DrawerSection>
      ) : null}

      {editing ? (
        <DrawerSection>
          <DrawerDanger title="Remove this indexer" description="Libraries routed only to it will need another indexer." action={<Button type="button" variant="destructive" size="sm" onClick={onRemove} disabled={busy !== null}>Remove…</Button>} />
        </DrawerSection>
      ) : null}
    </>
  );
}

function ClientDrawerBody({
  form,
  setForm,
  editing,
  errors,
  clearError,
  mappings,
  newMapping,
  setNewMapping,
  busy,
  onAddMapping,
  onRemoveMapping,
  onTestMapping,
  onTest,
  onRemove
}: {
  form: ClientForm;
  setForm: (updater: (current: ClientForm) => ClientForm) => void;
  editing: DownloadClientItem | null;
  errors: Record<string, string>;
  clearError: (key: string) => void;
  mappings: DownloadClientPathMappingItem[];
  newMapping: { remotePath: string; localPath: string };
  setNewMapping: (value: { remotePath: string; localPath: string }) => void;
  busy: string | null;
  onAddMapping: () => void;
  onRemoveMapping: (mappingId: string) => void;
  onTestMapping: (mappingId: string) => void;
  onTest: () => void;
  onRemove: () => void;
}) {
  const preset = CLIENT_PRESETS.find((item) => item.protocol === form.protocol);
  const chip = editing ? healthChip(editing) : null;
  const sameCategory = form.moviesCategory && form.moviesCategory === form.tvCategory;

  function choosePreset(protocol: string) {
    const next = CLIENT_PRESETS.find((item) => item.protocol === protocol);
    if (!next) return;
    setForm((current) => ({
      ...current,
      protocol,
      port: String(next.defaultPort),
      moviesCategory: next.defaultMoviesCategory,
      tvCategory: next.defaultTvCategory,
      name: current.name.trim() && !CLIENT_PRESETS.some((item) => item.label === current.name) ? current.name : next.label
    }));
  }

  return (
    <>
      <DrawerSection title="Basics">
        <FieldRow>
          <Field label="Name" error={errors.name}>
            <Input value={form.name} onChange={(event) => { clearError("name"); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder={preset?.label ?? "Download client"} autoComplete="off" />
          </Field>
          <Field label="Client" help={editing ? undefined : preset?.setupHint}>
            <Select value={form.protocol} disabled={Boolean(editing)} onChange={(event) => choosePreset(event.target.value)} options={CLIENT_PRESETS.map((item) => ({ value: item.protocol, label: `${item.label} · ${item.kind}` }))} />
          </Field>
        </FieldRow>
        <FieldRow>
          <Field label="Host or IP" error={errors.host}>
            <PresetField value={form.host} onChange={(value) => { clearError("host"); setForm((current) => ({ ...current, host: value })); }} options={HOST_OPTIONS} customLabel="Custom host / IP" customPlaceholder="Hostname or IP address" />
          </Field>
          <Field label="Port" error={errors.port}>
            <PresetField inputType="number" value={form.port} onChange={(value) => { clearError("port"); setForm((current) => ({ ...current, port: value })); }} options={PORT_OPTIONS} customLabel="Custom port" customPlaceholder="Port number" />
          </Field>
        </FieldRow>
        <FieldRow>
          <Field label="Username" optional>
            <Input value={form.username} onChange={(event) => setForm((current) => ({ ...current, username: event.target.value }))} autoComplete="off" />
          </Field>
          <Field label={preset?.authMode === "API key" ? "API key" : "Password"} optional help={editing ? "Stored encrypted. Leave blank to keep the current one." : preset?.authMode === "API key" ? "From the client's settings page." : undefined}>
            <Input type="password" value={form.password} onChange={(event) => setForm((current) => ({ ...current, password: event.target.value }))} placeholder={editing ? "••••••••  (unchanged)" : ""} autoComplete="new-password" />
          </Field>
        </FieldRow>
        <SwitchRow label="Enabled" description="Deluno may send approved releases to this client." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
      </DrawerSection>

      <DrawerSection title="Categories" aside="keep Movies and TV apart in the client">
        <FieldRow>
          <Field label="Movies category" help="The label or category your client uses for movie downloads.">
            <PresetField value={form.moviesCategory} onChange={(value) => setForm((current) => ({ ...current, moviesCategory: value }))} options={CATEGORY_OPTIONS} customLabel="Custom movie category" customPlaceholder="Download-client category" />
          </Field>
          <Field label="TV category" help={sameCategory ? undefined : "Should differ from the movies category."} error={sameCategory ? "Same as movies — downloads will mix in the client." : undefined}>
            <PresetField value={form.tvCategory} onChange={(value) => setForm((current) => ({ ...current, tvCategory: value }))} options={TV_CATEGORY_OPTIONS} customLabel="Custom TV category" customPlaceholder="Download-client category" />
          </Field>
        </FieldRow>
      </DrawerSection>

      {editing ? (
        <DrawerSection title="File locations" aside={mappings.length ? `${mappings.length} link${mappings.length === 1 ? "" : "s"}` : "only when paths differ"}>
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
            Configure the completed-download folder in the client itself. Add a link only when the same files have a different path for Deluno — for example a Docker client reports <code className="font-mono">/downloads/complete</code> while Deluno reads <code className="font-mono">D:\Downloads\complete</code>.
          </p>
          {mappings.length ? (
            <div className="grid gap-2">
              {mappings.map((mapping) => (
                <div key={mapping.id} className="flex min-h-10 items-center gap-2 rounded-[10px] border border-hairline px-[var(--field-pad-x)] font-mono text-[length:var(--type-caption)]">
                  <span className="min-w-0 flex-1 truncate text-muted-foreground" title={mapping.remotePath}>{mapping.remotePath}</span>
                  <span aria-hidden className="text-primary">→</span>
                  <span className="min-w-0 flex-1 truncate text-foreground" title={mapping.localPath}>{mapping.localPath}</span>
                  <Button type="button" variant="outline" size="sm" className="h-7 px-2 font-sans" onClick={() => onTestMapping(mapping.id)} disabled={busy !== null}>
                    {busy === `mapping:test:${mapping.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
                    Test
                  </Button>
                  <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Remove ${mapping.remotePath} link`} onClick={() => onRemoveMapping(mapping.id)} disabled={busy !== null}>
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                </div>
              ))}
            </div>
          ) : null}
          <FieldRow>
            <Field label={`Path reported by ${editing.name}`}>
              <Input value={newMapping.remotePath} onChange={(event) => setNewMapping({ ...newMapping, remotePath: event.target.value })} placeholder="/downloads/complete" className="font-mono text-[length:var(--type-caption)]" />
            </Field>
            <Field label="Same path as Deluno sees it">
              <Input value={newMapping.localPath} onChange={(event) => setNewMapping({ ...newMapping, localPath: event.target.value })} placeholder="D:\Downloads\complete" className="font-mono text-[length:var(--type-caption)]" />
            </Field>
          </FieldRow>
          <Button type="button" variant="outline" size="sm" className="w-max" onClick={onAddMapping} disabled={busy !== null || !newMapping.remotePath.trim() || !newMapping.localPath.trim()}>
            {busy === "mapping:add" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plus className="h-3.5 w-3.5" />}
            Link paths
          </Button>
        </DrawerSection>
      ) : null}

      {editing && chip ? (
        <DrawerSection title="Health">
          <dl className="grid grid-cols-[120px_1fr] items-center gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
            <dt className="text-muted-foreground">Last test</dt>
            <dd className="flex items-center gap-2"><Chip tone={chip.tone}>{chip.label}</Chip><span className="text-muted-foreground">{relative(editing.lastHealthTestUtc)}{editing.lastHealthLatencyMs != null ? ` · ${editing.lastHealthLatencyMs} ms` : ""}</span></dd>
            {editing.lastHealthMessage ? (<><dt className="text-muted-foreground">Message</dt><dd className="text-foreground">{editing.lastHealthMessage}</dd></>) : null}
          </dl>
          <Button type="button" variant="outline" size="sm" className="w-max" onClick={onTest} disabled={busy !== null}>
            {busy === `test:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wifi className="h-3.5 w-3.5" />}
            Test connection
          </Button>
        </DrawerSection>
      ) : null}

      {editing ? (
        <DrawerSection>
          <DrawerDanger title="Remove this client" description="Downloads already in the client are left alone." action={<Button type="button" variant="destructive" size="sm" onClick={onRemove} disabled={busy !== null}>Remove…</Button>} />
        </DrawerSection>
      ) : null}
    </>
  );
}

function RoutingDrawerBody({
  library,
  indexers,
  clients,
  sources,
  targets,
  onToggleSource,
  onToggleClient
}: {
  library: LibraryItem;
  indexers: IndexerItem[];
  clients: DownloadClientItem[];
  sources: string[];
  targets: string[];
  onToggleSource: (id: string, on: boolean) => void;
  onToggleClient: (id: string, on: boolean) => void;
}) {
  const isTv = library.mediaType === "tv";
  const relevantIndexers = indexers.filter((item) => (item.mediaScope ?? "both") === "both" || (item.mediaScope ?? "both") === (isTv ? "tv" : "movies"));

  return (
    <>
      <DrawerSection title="Indexers" aside={`${sources.length} of ${relevantIndexers.length} · only ${isTv ? "TV" : "movie"}-capable indexers are listed`}>
        {relevantIndexers.length ? (
          <div className="grid gap-2">
            {relevantIndexers.map((item) => {
              const chip = healthChip(item);
              return (
                <div key={item.id} className="flex min-h-10 items-center justify-between gap-[var(--grid-gap)] rounded-[10px] border border-hairline px-[var(--field-pad-x)]">
                  <label htmlFor={`route-src-${item.id}`} className="min-w-0 cursor-pointer">
                    <span className="block truncate text-[length:var(--type-body-sm)] font-medium text-foreground">{item.name}</span>
                    <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{protocolLabel(item.protocol)} · priority {item.priority}</span>
                  </label>
                  <span className="flex items-center gap-3">
                    <Chip tone={chip.tone}>{chip.label}</Chip>
                    <Switch id={`route-src-${item.id}`} size="sm" checked={sources.includes(item.id)} onCheckedChange={(on) => onToggleSource(item.id, on)} />
                  </span>
                </div>
              );
            })}
          </div>
        ) : (
          <p className="text-[length:var(--type-caption)] text-muted-foreground">No {isTv ? "TV" : "movie"}-capable indexers yet. Add one under Indexers.</p>
        )}
        {sources.length === 0 && relevantIndexers.length ? <p className="text-[length:var(--type-caption)] text-warning">No indexers selected — Deluno can't look for releases for this library.</p> : null}
      </DrawerSection>
      <DrawerSection title="Download clients" aside={`${targets.length} of ${clients.length}`}>
        {clients.length ? (
          <div className="grid gap-2">
            {clients.map((item) => {
              const chip = healthChip(item);
              const category = isTv ? item.tvCategory ?? item.categoryTemplate ?? "" : item.moviesCategory ?? item.categoryTemplate ?? "";
              return (
                <div key={item.id} className="flex min-h-10 items-center justify-between gap-[var(--grid-gap)] rounded-[10px] border border-hairline px-[var(--field-pad-x)]">
                  <label htmlFor={`route-cli-${item.id}`} className="min-w-0 cursor-pointer">
                    <span className="block truncate text-[length:var(--type-body-sm)] font-medium text-foreground">{item.name}</span>
                    <span className="block truncate text-[length:var(--type-caption)] text-muted-foreground">{protocolLabel(item.protocol)}{category ? ` · category ${category}` : ""}</span>
                  </label>
                  <span className="flex items-center gap-3">
                    <Chip tone={chip.tone}>{chip.label}</Chip>
                    <Switch id={`route-cli-${item.id}`} size="sm" checked={targets.includes(item.id)} onCheckedChange={(on) => onToggleClient(item.id, on)} />
                  </span>
                </div>
              );
            })}
          </div>
        ) : (
          <p className="text-[length:var(--type-caption)] text-muted-foreground">No download clients yet. Add one under Download clients.</p>
        )}
        {targets.length === 0 && sources.length > 0 && clients.length ? <p className="text-[length:var(--type-caption)] text-warning">No download client selected — approved releases have nowhere to go.</p> : null}
      </DrawerSection>
    </>
  );
}

function sameSet(a: string[], b: string[]) {
  if (a.length !== b.length) return false;
  const set = new Set(a);
  return b.every((item) => set.has(item));
}

import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { defineSources, queryClient, sourceLoader, useRouteSources, type RouteSourceData, type RouteSourceState } from "../lib/route-cache";
import { sharedSources } from "../lib/shared-sources";
import { RouteSourceGate } from "../components/shell/route-source-gate";
import { ListChecks, Loader2, Plus, Wifi } from "lucide-react";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  readValidationProblem,
  type DownloadClientItem,
  type DownloadClientCategoryCheckResult,
  type DownloadClientPathMappingItem,
  type DownloadClientTelemetrySnapshot,
  type DownloadTelemetryOverview,
  type IndexerItem,
  type IndexerScoreboardSnapshot,
  type LibraryItem,
  type LibraryRoutingSnapshot,
  type OutboundThrottleHostState,
  type OutboundThrottleSnapshot,
  type PlatformSettingsSnapshot,
  type ProxyItem,
  type QbittorrentBlocklistSyncState
} from "../lib/api";
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";
import { useCoalescedSourceRefresh } from "../lib/live-refresh";
import { patchTelemetryProgress } from "../lib/download-telemetry";
import { RealtimeGroups, useLiveEvent, useLiveWatching, useSignalREvent } from "../lib/use-signalr";
import { configurationNavAreas } from "../components/app/settings-shell";
import { DetectedComponents, type BundledComponentStatus } from "../components/app/bundled-components-detected";
import { Button } from "../components/ui/button";
import { ConnectionTestResult, type ConnectionTestOutcome } from "./connections/connection-test-result";
import { Chip } from "../components/ui/chip";
import { Checkbox } from "../components/ui/checkbox";
import { Input } from "../components/ui/input";
import { Select } from "../components/ui/select";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerFooter, type DrawerSaveState } from "../components/ui/drawer";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { ConnectionVersionBeside } from "../components/app/connection-version";
import { connectionVersionLine, useConnectionVersions, versionKey } from "../lib/connection-versions";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Switch } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { CLIENT_PRESETS, type MediaScope } from "./connections/presets";
import { STRICT_SHARING, clientFormErrors, clientFormFrom, clientsForPathMapping, emptyClientForm, emptyIndexerForm, emptyProxyForm, indexerFormErrors, indexerFormFrom, proxyFormErrors, proxyFormFrom, sameClient, sameIndexer, sameProxy, sameSet, sharingRuleFrom, type ClientForm, type DrawerState, type IndexerForm, type ProxyForm, type Section } from "./connections/forms";
import { formatSeconds, healthChip, indexerHost, indexerReadiness, protocolLabel, relative, scopeLabel, unusedChip } from "./connections/format";
import { ClientDrawerBody } from "./connections/client-drawer-body";
import { IndexerDrawerBody } from "./connections/indexer-drawer-body";
import { ProxyDrawerBody } from "./connections/proxy-drawer-body";
import { RoutingDrawerBody } from "./connections/routing-drawer-body";
import { routingStatus, useRoutingChecks } from "./connections/routing-status";
import { RoutingFixLink } from "./connections/preferences-link";
import { IndexerScoreboard } from "./connections/indexer-scoreboard";
import {
  emptyIndexerFilters,
  filterIndexers,
  IndexerBulkBar,
  IndexerFilterBar,
  IndexerFilterChips,
  IndexerSelectAllCheckbox,
  indexerSortValue,
  type IndexerBulkScope,
  type IndexerFilters
} from "./connections/indexer-list-controls";
import { toneSortValue } from "../lib/status-tones";
import { ConnectionSelectAll, ConnectionSelectButton, ConnectionSelectionBar, ConnectionTagsDialog } from "../components/app/connection-tags-dialog";
import { useRowSelection } from "../lib/connection-tags";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

const TABS = configurationNavAreas.find((area) => area.to === "/indexers/indexers")?.items ?? [];

const PROXY_TYPE_LABELS: Record<string, string> = {
  http: "HTTP",
  socks4: "SOCKS4",
  socks5: "SOCKS5",
  flaresolverr: "FlareSolverr / Byparr"
};

type RouteCategories = Record<string, string>;
/** Per indexer, the tags a title must carry (required) or must not carry (excluded) for this library to search it. */
type RouteSourceTags = Record<string, { required: string; excluded: string }>;

function sameRouteSourceTags(left: RouteSourceTags, right: RouteSourceTags, ids: string[]) {
  return ids.every((id) => (left[id]?.required ?? "") === (right[id]?.required ?? "") && (left[id]?.excluded ?? "") === (right[id]?.excluded ?? ""));
}

function sameRouteCategories(left: RouteCategories, right: RouteCategories) {
  const keys = new Set([...Object.keys(left), ...Object.keys(right)]);
  return [...keys].every((key) => (left[key] ?? "") === (right[key] ?? ""));
}

/** Which tab of Find & Download an address opens. */
export function indexersSectionFor(pathname: string): Section {
  return pathname.endsWith("/scoreboard") ? "scoreboard" : pathname.endsWith("/download-clients") ? "clients" : pathname.endsWith("/path-mappings") ? "pathMappings" : pathname.endsWith("/library-routing") ? "routing" : pathname.endsWith("/proxies") ? "proxies" : "indexers";
}

const emptyOutboundThrottle: OutboundThrottleSnapshot = { hosts: [] };

/**
 * One set of cached sources behind every tab of Find & Download (#1081). A tab
 * reads only what it shows: routing is read on Library Routing, the
 * scoreboard on Scoreboard, the mappings on the two tabs that list them. The
 * lists every tab needs (indexers, clients, libraries) are shared, so moving
 * between tabs asks for nothing that is already there.
 */
export const indexersSources = defineSources({
  indexers: sharedSources.indexers,
  clients: sharedSources.downloadClients,
  libraries: sharedSources.libraries,
  settings: { ...sharedSources.settings, fallback: emptyPlatformSettingsSnapshot },
  telemetry: {
    key: ["src", "telemetry-soft"],
    label: "Download activity",
    fetch: () => fetchJson<DownloadTelemetryOverview>("/api/download-clients/telemetry").catch(() => null),
    fallback: null as DownloadTelemetryOverview | null,
    sections: ["clients"]
  },
  outboundThrottle: {
    key: ["src", "outbound-throttle"],
    label: "Search limits",
    fetch: () => fetchJson<OutboundThrottleSnapshot>("/api/integrations/outbound-throttle").catch(() => emptyOutboundThrottle),
    fallback: emptyOutboundThrottle,
    sections: ["indexers"]
  },
  scoreboard: {
    key: ["src", "indexer-scoreboard-30"],
    label: "Indexer scoreboard",
    fetch: () => fetchJson<IndexerScoreboardSnapshot>("/api/indexers/scoreboard?days=30").catch(() => emptyScoreboard()),
    fallback: emptyScoreboard(),
    sections: ["scoreboard"],
    requiredIn: ["scoreboard"]
  },
  proxies: {
    key: ["src", "indexer-proxies"],
    label: "Proxies",
    fetch: () => fetchJson<ProxyItem[]>("/api/indexer-proxies").catch(() => [] as ProxyItem[]),
    fallback: [] as ProxyItem[],
    sections: ["indexers", "proxies"],
    requiredIn: ["proxies"]
  },
  // #822: the qBittorrent "Excluded file names" sync's own last-attempt
  // state per client, for the Connections screen's client drawer.
  qbittorrentBlocklistSync: {
    key: ["src", "qbittorrent-blocklist-sync"],
    label: "qBittorrent file-name sync",
    fetch: () => fetchJson<Record<string, QbittorrentBlocklistSyncState>>("/api/download-health/content-blocklist/qbittorrent-sync-status").catch(() => ({} as Record<string, QbittorrentBlocklistSyncState>)),
    fallback: {} as Record<string, QbittorrentBlocklistSyncState>,
    sections: ["clients"]
  },
  // The one shared list (#809): every mapping, across every host, rather
  // than each client's own drawer view flattened together, which would
  // show the same mapping twice the moment two clients share a host.
  pathMappings: {
    key: ["src", "path-mappings"],
    label: "Remote path mappings",
    fetch: () => fetchJson<DownloadClientPathMappingItem[]>("/api/download-clients/path-mappings").catch(() => [] as DownloadClientPathMappingItem[]),
    fallback: [] as DownloadClientPathMappingItem[],
    sections: ["clients", "pathMappings"],
    requiredIn: ["pathMappings"]
  },
  // Which libraries search which sources: needs the library list first, then
  // one small request per library, all in the background of the tabs that
  // only mention it.
  routing: {
    key: ["src", "library-routing"],
    label: "Library routing",
    fetch: async () => {
      const libraries = await queryClient.fetchQuery({ queryKey: sharedSources.libraries.key, queryFn: sharedSources.libraries.fetch, staleTime: 3_000 });
      return Promise.all(
        libraries.map((lib) =>
          fetchJson<LibraryRoutingSnapshot>(`/api/libraries/${lib.id}/routing`).catch((): LibraryRoutingSnapshot => ({ libraryId: lib.id, libraryName: lib.name, sources: [], downloadClients: [] }))
        )
      );
    },
    fallback: [] as LibraryRoutingSnapshot[],
    requiredIn: ["routing"]
  }
});

export const indexersLoader = sourceLoader(indexersSources, (request) => indexersSectionFor(new URL(request.url).pathname));

function emptyScoreboard(): IndexerScoreboardSnapshot {
  return {
    windowDays: 30,
    fromUtc: new Date().toISOString(),
    toUtc: new Date().toISOString(),
    activeIndexers: 0,
    totalIndexers: 0,
    totalQueries: 0,
    totalGrabs: 0,
    successfulGrabs: 0,
    conversionRate: null,
    insight: "No indexer activity has been recorded yet.",
    indexers: []
  };
}

async function send(url: string, method: string, body?: unknown, failure = "Request failed.", signal?: AbortSignal) {
  const response = await authedFetch(url, { method, signal, headers: body === undefined ? undefined : { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok && response.status !== 204) {
    const problem = await readValidationProblem(response.clone()).catch(() => null);
    const detail = problem?.errors ? Object.values(problem.errors).flat()[0] : problem?.title;
    throw new Error(detail || failure);
  }
  return response;
}

/**
 * The body of PUT/POST /api/indexers — shared by Save and Test (#626).
 *
 * Tags decide which titles this indexer is searched for (#990): blank
 * searches it for any title, and a tagged indexer is searched only for a
 * title sharing one of its tags — the same rule already applied to download
 * clients (#808).
 */
export function buildIndexerSavePayload(form: IndexerForm, initial: IndexerForm, editing: boolean) {
  return {
    name: form.name.trim(),
    // A name given is kept, and a blank one is worked out from the address (#1248).
    protocol: form.protocol,
    // Privacy is not sent at all. Deluno branches on none of it, nobody
    // can set it here, and guessing "private" for every hand-added source
    // was inventing provenance. It stays a fact a migration imports from
    // the app you came from, where it seeds the sharing rule that does
    // change behaviour.
    baseUrl: form.baseUrl.trim(),
    apiKey: form.apiKey.trim() || undefined,
    apiPath: form.apiPath.trim() || null,
    clearApiPath: editing && !form.apiPath.trim(),
    additionalParameters: form.additionalParameters.trim() || null,
    clearAdditionalParameters: editing && !form.additionalParameters.trim(),
    animeCategories: form.animeCategories.trim() || null,
    clearAnimeCategories: editing && !form.animeCategories.trim(),
    priority: Number(form.priority || 10),
    requestIntervalSeconds: form.requestIntervalSeconds.trim() ? Number(form.requestIntervalSeconds) : null,
    clearRequestInterval: editing && !form.requestIntervalSeconds.trim(),
    minimumAgeMinutes: form.minimumAgeMinutes.trim() ? Number(form.minimumAgeMinutes) : null,
    clearMinimumAge: editing && !form.minimumAgeMinutes.trim(),
    retentionDays: form.retentionDays.trim() ? Number(form.retentionDays) : null,
    clearRetention: editing && !form.retentionDays.trim(),
    maximumSizeMb: form.maximumSizeMb.trim() ? Number(form.maximumSizeMb) : null,
    clearMaximumSize: editing && !form.maximumSizeMb.trim(),
    minimumSeeders: form.minimumSeeders.trim() ? Number(form.minimumSeeders) : null,
    clearMinimumSeeders: editing && !form.minimumSeeders.trim(),
    preferIndexerFlags: form.preferIndexerFlags.trim() || null,
    clearPreferIndexerFlags: editing && !form.preferIndexerFlags.trim(),
    requiredWords: form.requiredWords.trim() || null,
    clearRequiredWords: editing && !form.requiredWords.trim(),
    ignoredWords: form.ignoredWords.trim() || null,
    clearIgnoredWords: editing && !form.ignoredWords.trim(),
    multiLanguages: form.multiLanguages.trim() || null,
    clearMultiLanguages: editing && !form.multiLanguages.trim(),
    vipExpiresUtc: form.vipExpiresUtc.trim() ? new Date(`${form.vipExpiresUtc.trim()}T00:00:00.000Z`).toISOString() : null,
    clearVipExpiry: editing && !form.vipExpiresUtc.trim(),
    queryLimit: form.queryLimit.trim() ? Number(form.queryLimit) : null,
    clearQueryLimit: editing && !form.queryLimit.trim(),
    grabLimit: form.grabLimit.trim() ? Number(form.grabLimit) : null,
    clearGrabLimit: editing && !form.grabLimit.trim(),
    limitsUnit: form.limitsUnit,
    preferMagnetUrl: form.preferMagnetUrl,
    seasonPackSearchMode: form.seasonPackSearchMode,
    availabilityDelayDays: form.availabilityDelayDays.trim() ? Number(form.availabilityDelayDays) : null,
    clearAvailabilityDelay: editing && !form.availabilityDelayDays.trim(),
    rssEnabled: form.rssEnabled,
    automaticSearchEnabled: form.automaticSearchEnabled,
    interactiveSearchEnabled: form.interactiveSearchEnabled,
    proxyId: form.proxyId || null,
    clearProxy: editing && !form.proxyId,
    // A built-in tracker (#606): which one, its settings, and only the
    // secrets typed now; a blank one keeps what is saved.
    ...(form.protocol === "tracker"
      ? {
          definitionId: form.definitionId,
          definitionSettings: form.trackerSettings,
          definitionSecrets: Object.fromEntries(Object.entries(form.trackerSecrets).filter(([, value]) => value.trim()))
        }
      : {}),
    categories: form.categories,
    tags: form.tags.trim(),
    mediaScope: form.scope,
    isEnabled: form.isEnabled,
    // Only written when the user actually answered the question
    // differently. A source may carry a hand-tuned rule this one control
    // cannot express, and saving an unrelated field should not flatten it
    // back to the canned one (#288).
    ...(form.sharingRule === initial.sharingRule
      ? {}
      : form.sharingRule === "strict"
        ? {
            sharingMode: "share-then-tidy",
            sharingForHours: STRICT_SHARING.forHours,
            sharingUntilRatio: STRICT_SHARING.untilRatio,
            sharingStuckAction: "keep-waiting",
            sharingStuckAfterDays: STRICT_SHARING.stuckAfterDays,
            clearSharingPolicy: false
          }
        : form.sharingRule === "custom"
          ? {
              sharingMode: "share-then-tidy",
              sharingForHours: form.sharingSeedHours.trim() ? Number(form.sharingSeedHours) : null,
              sharingUntilRatio: form.sharingSeedRatio.trim() ? Number(form.sharingSeedRatio) : null,
              sharingStuckAction: "keep-waiting",
              sharingStuckAfterDays: STRICT_SHARING.stuckAfterDays,
              clearSharingPolicy: false
            }
          : { clearSharingPolicy: true }),
    // Independent of the rule above: a season pack from this source can seed
    // for longer even when the rest of the sharing rule is inherited (#739).
    seasonPackSeedForHours: form.seasonPackSeedForHours.trim() ? Number(form.seasonPackSeedForHours) : null,
    clearSeasonPackSeedForHours: editing && !form.seasonPackSeedForHours.trim()
  };
}

/** The body of PUT/POST /api/download-clients — shared by Save and Test, same reason as buildIndexerSavePayload (#626). */
export function buildClientSavePayload(form: ClientForm, saved?: ClientForm) {
  const preset = CLIENT_PRESETS.find((item) => item.protocol === form.protocol);
  const port = Number(form.port || preset?.defaultPort || 8080);
  const urlBase = form.urlBase.trim().replace(/^\/|\/$/g, "");
  // Saving a saved client sends its address only when somebody edited it. The host, port and URL stay exactly as
  // stored otherwise, so changing the priority cannot rewrite them (golden path rc.30.9).
  const addressEdited = !saved
    || form.host.trim() !== saved.host.trim()
    || form.port.trim() !== saved.port.trim()
    || form.useSsl !== saved.useSsl
    || form.urlBase.trim() !== saved.urlBase.trim();
  const address = addressEdited
    ? {
        host: form.host.trim(),
        port,
        endpointUrl: `${form.useSsl ? "https" : "http"}://${form.host.trim()}:${port}${urlBase ? `/${urlBase}` : ""}`,
        useSsl: form.useSsl,
        urlBase: urlBase || undefined
      }
    : {};
  return {
    name: form.name.trim(),
    protocol: form.protocol,
    ...address,
    username: form.username || undefined,
    password: form.password || undefined,
    moviesCategory: form.moviesCategory,
    tvCategory: form.tvCategory,
    categoryTemplate: form.moviesCategory,
    priority: Number(form.priority || 1),
    isEnabled: form.isEnabled,
    addPaused: form.addPaused,
    tags: form.tags.trim(),
    removeCompleted: form.removeCompleted,
    removeFailed: form.removeFailed,
    skipHashCheck: form.skipHashCheck,
    sequentialDownload: form.sequentialDownload,
    firstLastPiecePrio: form.firstLastPiecePrio,
    shareRatioLimit: form.shareRatioLimit.trim() ? Number(form.shareRatioLimit) : null,
    clearShareRatioLimit: !form.shareRatioLimit.trim(),
    shareSeedingTimeLimitMinutes: form.shareSeedingTimeLimitMinutes.trim() ? Number(form.shareSeedingTimeLimitMinutes) : null,
    clearShareSeedingTimeLimitMinutes: !form.shareSeedingTimeLimitMinutes.trim(),
    shareInactiveSeedingTimeLimitMinutes: form.shareInactiveSeedingTimeLimitMinutes.trim() ? Number(form.shareInactiveSeedingTimeLimitMinutes) : null,
    clearShareInactiveSeedingTimeLimitMinutes: !form.shareInactiveSeedingTimeLimitMinutes.trim(),
    location: form.location.trim() || null,
    clearLocation: !form.location.trim(),
    contentLayout: form.contentLayout || "default"
  };
}

/** The body of PUT/POST /api/indexer-proxies — a proxy has no separate Test action, so this is only ever a save. */
export function buildProxySavePayload(form: ProxyForm) {
  return {
    proxyType: form.proxyType,
    address: form.address.trim(),
    username: form.username.trim() || null,
    // Blank means "keep the saved password"; a proxy has no separate clear action.
    password: form.password.trim() || undefined,
    isEnabled: form.isEnabled
  };
}

export function IndexersPage() {
  const { pathname } = useLocation();
  const state = useRouteSources(indexersSources, indexersSectionFor(pathname));
  return (
    <RouteSourceGate state={state}>
      {() => <IndexersPageBody data={state.data} revalidator={state.revalidator} />}
    </RouteSourceGate>
  );
}

/**
 * Keeps the Download Clients tab's figures moving (#1179): each pushed progress reading is written
 * into the telemetry the tab already holds (that download's progress and speed, and the client's
 * speed), so nothing is fetched for it. Mounted only on that tab, because watching progress makes
 * the server read the clients every second.
 */
function ClientProgressFeed() {
  useLiveWatching();
  useSignalREvent("DownloadProgress", RealtimeGroups.Queue, (reading) => {
    queryClient.setQueryData<DownloadTelemetryOverview | null>(indexersSources.telemetry.key, (current) => patchTelemetryProgress(current, reading) ?? null);
  });
  return null;
}

function IndexersPageBody({ data, revalidator }: { data: RouteSourceData<typeof indexersSources>; revalidator: RouteSourceState<typeof indexersSources>["revalidator"] }) {
  const { clients, pathMappings, indexers, libraries, routing, settings, telemetry, outboundThrottle, scoreboard, proxies, qbittorrentBlocklistSync } = data;
  const location = useLocation();
  const navigate = useNavigate();
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
  const [liveOutboundThrottle, setLiveOutboundThrottle] = useState(outboundThrottle);

  const section: Section = indexersSectionFor(location.pathname);

  useEffect(() => {
    setLiveOutboundThrottle(outboundThrottle);
  }, [outboundThrottle]);

  // Find & Download follows the server (#1179): each event names the lists it moves and only those
  // are read again. Nothing asks on a timer; a reconnect reads the screen once.
  const refresh = useCoalescedSourceRefresh();
  useSignalREvent("OutboundThrottleChanged", RealtimeGroups.Dashboard, () => refresh(indexersSources.outboundThrottle));
  useSignalREvent("IndexerChanged", RealtimeGroups.Dashboard, () => refresh(indexersSources.indexers, indexersSources.scoreboard, indexersSources.routing));
  useSignalREvent("IndexerProxyChanged", RealtimeGroups.Dashboard, () => refresh(indexersSources.proxies));
  useSignalREvent("DownloadClientChanged", RealtimeGroups.Dashboard, () => refresh(indexersSources.clients, indexersSources.pathMappings, indexersSources.routing, indexersSources.telemetry));
  useSignalREvent("HealthChanged", RealtimeGroups.Dashboard, () => refresh(indexersSources.indexers, indexersSources.clients));
  useSignalREvent("LibraryChanged", RealtimeGroups.Dashboard, () => refresh(indexersSources.libraries, indexersSources.routing));
  useSignalREvent("SettingsChanged", RealtimeGroups.Dashboard, () => refresh(indexersSources.settings));
  useSignalREvent("ConnectionChecked", RealtimeGroups.Dashboard, () => refresh(indexersSources.indexers, indexersSources.clients));
  useLiveEvent("ClientStateChanged", () => refresh(indexersSources.clients, indexersSources.telemetry));
  useSignalREvent("QueueItemAdded", RealtimeGroups.Queue, () => refresh(indexersSources.telemetry));
  useSignalREvent("QueueItemRemoved", RealtimeGroups.Queue, () => refresh(indexersSources.telemetry));
  useSignalREvent("QueueItemStatusChanged", RealtimeGroups.Queue, () => refresh(indexersSources.telemetry));

  const telemetryByClient = useMemo(() => new Map<string, DownloadClientTelemetrySnapshot>(telemetry?.clients.map((item) => [item.clientId, item]) ?? []), [telemetry]);
  const { versions } = useConnectionVersions();
  const throttleByHost = useMemo(() => new Map<string, OutboundThrottleHostState>(liveOutboundThrottle.hosts.map((item) => [item.host.toLowerCase(), item])), [liveOutboundThrottle]);
  const mappingsByClient = useMemo(() => {
    const map = new Map<string, DownloadClientPathMappingItem[]>();
    for (const mapping of pathMappings) map.set(mapping.downloadClientId, [...(map.get(mapping.downloadClientId) ?? []), mapping]);
    return map;
  }, [pathMappings]);
  function clientsForMapping(mapping: DownloadClientPathMappingItem): DownloadClientItem[] {
    return clientsForPathMapping(mapping, clients);
  }

  const [indexerFilters, setIndexerFilters] = useState<IndexerFilters>(emptyIndexerFilters);
  const [selectMode, setSelectMode] = useState(false);
  const [selectedIndexerIds, setSelectedIndexerIds] = useState<Set<string>>(new Set());
  const [confirmBulkRemove, setConfirmBulkRemove] = useState(false);
  const [bulkBusy, setBulkBusy] = useState(false);
  const [tagsFor, setTagsFor] = useState<"indexer" | "downloadClient" | null>(null);
  const clientSelection = useRowSelection();

  const visibleIndexers = useMemo(() => filterIndexers(indexers, indexerFilters), [indexers, indexerFilters]);

  function toggleSelectMode() {
    setSelectMode((current) => !current);
    setSelectedIndexerIds(new Set());
  }
  function toggleIndexerSelected(id: string) {
    setSelectedIndexerIds((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }
  async function bulkSetEnabled(isEnabled: boolean) {
    const ids = [...selectedIndexerIds];
    if (!ids.length) return;
    setBulkBusy(true);
    try {
      await Promise.all(ids.map((id) => send(`/api/indexers/${id}`, "PUT", { isEnabled })));
      toast.success(`${ids.length} indexer${ids.length === 1 ? "" : "s"} ${isEnabled ? "enabled" : "paused"}`);
      setSelectedIndexerIds(new Set());
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Bulk update failed.");
    } finally {
      setBulkBusy(false);
    }
  }
  async function bulkSetScope(scope: IndexerBulkScope) {
    const ids = [...selectedIndexerIds];
    if (!ids.length) return;
    setBulkBusy(true);
    try {
      await Promise.all(ids.map((id) => send(`/api/indexers/${id}`, "PUT", { mediaScope: scope })));
      toast.success(`${ids.length} indexer${ids.length === 1 ? "" : "s"} set to ${scopeLabel(scope)}`);
      setSelectedIndexerIds(new Set());
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Bulk update failed.");
    } finally {
      setBulkBusy(false);
    }
  }
  async function bulkRemove() {
    const ids = [...selectedIndexerIds];
    if (!ids.length) return;
    setBulkBusy(true);
    try {
      await Promise.all(ids.map((id) => send(`/api/indexers/${id}`, "DELETE")));
      toast.success(`${ids.length} indexer${ids.length === 1 ? "" : "s"} removed`);
      setSelectedIndexerIds(new Set());
      revalidator.revalidate();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Bulk remove failed.");
    } finally {
      setBulkBusy(false);
      setConfirmBulkRemove(false);
    }
  }

  const [drawer, setDrawer] = useState<DrawerState>({ kind: "closed" });
  const [indexerForm, setIndexerForm] = useState<IndexerForm>(emptyIndexerForm);
  const [indexerInitial, setIndexerInitial] = useState<IndexerForm>(emptyIndexerForm);
  const [clientForm, setClientForm] = useState<ClientForm>(emptyClientForm);
  const [clientInitial, setClientInitial] = useState<ClientForm>(emptyClientForm);
  const [proxyForm, setProxyForm] = useState<ProxyForm>(emptyProxyForm);
  const [proxyInitial, setProxyInitial] = useState<ProxyForm>(emptyProxyForm);
  const [routeSources, setRouteSources] = useState<string[]>([]);
  const [routeSourceTags, setRouteSourceTags] = useState<RouteSourceTags>({});
  const [routeClients, setRouteClients] = useState<string[]>([]);
  const [routeCategories, setRouteCategories] = useState<RouteCategories>({});
  const [categoryChecks, setCategoryChecks] = useState<Record<string, DownloadClientCategoryCheckResult>>({});
  const [routeInitial, setRouteInitial] = useState<{ sources: string[]; clients: string[]; categories: RouteCategories; sourceTags: RouteSourceTags }>({ sources: [], clients: [], categories: {}, sourceTags: {} });
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<ConnectionTestOutcome | null>(null);
  // Whether Add also routes the new indexer or client to the libraries it is for (#1135).
  const [routeNewIndexer, setRouteNewIndexer] = useState(true);
  const [routeNewClient, setRouteNewClient] = useState(true);
  // A result belongs to the drawer it was run in.
  const drawerKey = JSON.stringify(drawer);
  // A test run in the drawer before Add (#1134): kept with the form it tested, and sent with
  // the new indexer only if nothing was changed since, so the list shows it straight away.
  const testedNewIndexer = useRef<{ form: IndexerForm; health: { healthStatus: string; message: string; failureCategory: string | null; latencyMs: number | null } } | null>(null);
  useEffect(() => { setTestResult(null); testedNewIndexer.current = null; setRouteNewIndexer(true); setRouteNewClient(true); }, [drawerKey]);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [confirmRemove, setConfirmRemove] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  // The running connection test, so a Test that never answers can be cancelled (#1248).
  const testAbort = useRef<AbortController | null>(null);
  const [showKey, setShowKey] = useState(false);
  const [fineTuneOpen, setFineTuneOpen] = useState(false);
  const [newMapping, setNewMapping] = useState({ remotePath: "", localPath: "" });
  // The shared Remote Path Mappings screen (#809). `anchorClientId` is
  // whichever client the mapping is filed under — required by the existing
  // per-client add/remove API — and `editingId` is set while this form
  // stands in for an existing row: there is no update endpoint, so an edit
  // creates the replacement first and only deletes the original once that
  // succeeds, so a failed save never loses the row it was changing.
  const [sharedMappingForm, setSharedMappingForm] = useState({
    editingId: null as string | null,
    anchorClientId: "",
    host: "",
    remotePath: "",
    localPath: "",
    priority: "10"
  });
  // Set by openDetectedClient once a drawer opens pre-filled from a detected
  // component (#640), so it shows as tested before the person even reaches
  // Save — the same test a manual "Test" click runs, just run once for them.
  const [pendingAutoTest, setPendingAutoTest] = useState(false);
  useEffect(() => {
    if (!pendingAutoTest) return;
    setPendingAutoTest(false);
    void testForm();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingAutoTest]);

  const editingIndexer = drawer.kind === "indexer" && drawer.id ? indexers.find((item) => item.id === drawer.id) ?? null : null;
  const editingIndexerThrottle = editingIndexer ? throttleByHost.get(indexerHost(editingIndexer.baseUrl) ?? "") ?? null : null;
  const editingClient = drawer.kind === "client" && drawer.id ? clients.find((item) => item.id === drawer.id) ?? null : null;
  const editingProxy = drawer.kind === "proxy" && drawer.id ? proxies.find((item) => item.id === drawer.id) ?? null : null;
  const routingLibrary = drawer.kind === "routing" ? libraries.find((item) => item.id === drawer.libraryId) ?? null : null;

  const dirty = useMemo(() => {
    if (drawer.kind === "indexer") return !sameIndexer(indexerForm, indexerInitial);
    if (drawer.kind === "client") return !sameClient(clientForm, clientInitial);
    if (drawer.kind === "proxy") return !sameProxy(proxyForm, proxyInitial);
    if (drawer.kind === "routing") return !sameSet(routeSources, routeInitial.sources) || !sameSet(routeClients, routeInitial.clients) || !sameRouteCategories(routeCategories, routeInitial.categories) || !sameRouteSourceTags(routeSourceTags, routeInitial.sourceTags, routeSources);
    return false;
  }, [drawer.kind, indexerForm, indexerInitial, clientForm, clientInitial, proxyForm, proxyInitial, routeSources, routeClients, routeCategories, routeInitial]);
  const footerState: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  /**
   * A create drawer opens with values the user is entitled to accept as-is, so
   * its Save is gated on the form being *valid*, not on it being *dirty* (#293).
   * "Unchanged from the defaults" is not "nothing to save"; gating on dirty made
   * Add client an inert control on the one path a new user actually takes.
   * Submit runs the same field validation either way, so an incomplete create
   * form now says what is missing instead of doing nothing. Editing keeps the
   * dirty gate, where Save with no changes really is a no-op.
   */
  const creating = (drawer.kind === "indexer" || drawer.kind === "client" || drawer.kind === "proxy") && drawer.id === null;
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  const resetDrawerChrome = useCallback(() => {
    setSaveState(undefined);
    setFieldErrors({});
    setShowKey(false);
    setFineTuneOpen(false);
    setNewMapping({ remotePath: "", localPath: "" });
  }, []);
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
  function openProxy(item: ProxyItem | null) {
    const next = item ? proxyFormFrom(item) : emptyProxyForm();
    setDrawer({ kind: "proxy", id: item?.id ?? null });
    setProxyForm(next);
    setProxyInitial(next);
    resetDrawerChrome();
  }
  /**
   * Opens a new client already pointed at what the installer put on this
   * machine, or found already there (#640): host, port and the matching
   * preset, so the person only has to add credentials if the client needs
   * them. Auto-tests once the drawer has settled, the same test a manual
   * "Test" click would run, so the connection shows as tested before Save.
   */
  function openPrefilledClient(status: BundledComponentStatus, host = status.defaultHost, port = status.defaultPort, autoTest = true) {
    const preset = CLIENT_PRESETS.find((item) => item.protocol === status.kind);
    const next: ClientForm = {
      ...emptyClientForm(),
      protocol: status.kind,
      host,
      port: String(port),
      username: "",
      password: "",
      name: preset?.label ?? status.displayName,
      moviesCategory: preset?.defaultMoviesCategory ?? emptyClientForm().moviesCategory,
      tvCategory: preset?.defaultTvCategory ?? emptyClientForm().tvCategory
    };
    setDrawer({ kind: "client", id: null });
    setClientForm(next);
    setClientInitial(next);
    resetDrawerChrome();
    if (autoTest) setPendingAutoTest(true);
  }
  const openRouting = useCallback((library: LibraryItem) => {
    const snapshot = routing.find((item) => item.libraryId === library.id);
    const sources = snapshot?.sources.map((source) => source.indexerId) ?? [];
    const clientIds = snapshot?.downloadClients.map((client) => client.downloadClientId) ?? [];
    const categories = Object.fromEntries(snapshot?.downloadClients.map((client) => [client.downloadClientId, client.category ?? ""]) ?? []);
    setDrawer({ kind: "routing", libraryId: library.id });
    setRouteSources(sources);
    setRouteClients(clientIds);
    const sourceTags: RouteSourceTags = Object.fromEntries(snapshot?.sources.map((source) => [source.indexerId, { required: source.requiredTags ?? "", excluded: source.excludedTags ?? "" }]) ?? []);
    setRouteCategories(categories);
    setRouteSourceTags(sourceTags);
    setCategoryChecks({});
    setRouteInitial({ sources, clients: clientIds, categories, sourceTags });
    resetDrawerChrome();
  }, [resetDrawerChrome, routing]);

  useEffect(() => {
    if (section !== "routing" || drawer.kind !== "closed") return;
    const libraryId = new URLSearchParams(location.search).get("libraryId");
    if (!libraryId) return;
    const library = libraries.find((item) => item.id === libraryId);
    if (!library) return;
    openRouting(library);
    navigate("/indexers/library-routing", { replace: true });
  }, [drawer.kind, libraries, location.search, navigate, openRouting, section]);
  // A notice about a newer version opens that download client (#1118).
  useEffect(() => {
    if (section !== "clients" || drawer.kind !== "closed") return;
    const id = new URLSearchParams(location.search).get("connection");
    if (!id) return;
    const client = clients.find((item) => item.id === id);
    if (!client) return;
    openClient(client);
    navigate("/indexers/download-clients", { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [section, drawer.kind, clients, location.search, navigate]);
  function closeDrawer() {
    setDrawer({ kind: "closed" });
    setConfirmDiscard(false);
  }
  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  // What Save sends is what Test tries (#626).
  function indexerPayload() {
    return buildIndexerSavePayload(indexerForm, indexerInitial, drawer.kind === "indexer" && Boolean(drawer.id));
  }

  function clientPayload() {
    return buildClientSavePayload(clientForm);
  }

  /**
   * Tests what the drawer holds, before or without saving it (#626). A saved
   * item with nothing changed takes the saved test, which records its health.
   * Anything else, a new item or unsaved edits, is tried as a draft. Blank
   * secrets on an edit mean "keep what is saved", and the server fills them in.
   */
  async function testForm() {
    if (drawer.kind !== "indexer" && drawer.kind !== "client") return;
    const isIndexer = drawer.kind === "indexer";
    const errors = isIndexer ? indexerFormErrors(indexerForm, !drawer.id) : clientFormErrors(clientForm);
    setFieldErrors(errors);
    if (Object.keys(errors).length) return;
    const base = isIndexer ? "/api/indexers" : "/api/download-clients";
    const savedAndUnchanged = Boolean(drawer.id) && !dirty;
    setBusy("test:form");
    setTestResult(null);
    const abort = new AbortController();
    testAbort.current = abort;
    try {
      const response = savedAndUnchanged
        ? await send(`${base}/${drawer.id}/test`, "POST", undefined, "Test failed.", abort.signal)
        : await send(`${base}/test${drawer.id ? `?id=${encodeURIComponent(drawer.id)}` : ""}`, "POST", isIndexer ? indexerPayload() : clientPayload(), "Test failed.", abort.signal);
      const body = (await response.json()) as { healthStatus?: string; message?: string; lastHealthMessage?: string; failureCategory?: string | null; latencyMs?: number | null; caps?: { categories?: string | null; mediaScope?: string | null } | null };
      setTestResult({ healthStatus: body.healthStatus ?? "unknown", message: body.message ?? body.lastHealthMessage ?? "" });
      // #755: a generic Newznab/Torznab indexer's own t=caps response
      // pre-fills categories and search mode when adding it, editable
      // afterwards. Never overwrites an existing indexer's own settings.
      if (isIndexer && !drawer.id) {
        const tested: IndexerForm = body.caps
          ? { ...indexerForm, categories: body.caps.categories || indexerForm.categories, scope: (body.caps.mediaScope as MediaScope | undefined) ?? indexerForm.scope }
          : indexerForm;
        testedNewIndexer.current = { form: tested, health: { healthStatus: body.healthStatus ?? "unknown", message: body.message ?? "", failureCategory: body.failureCategory ?? null, latencyMs: body.latencyMs ?? null } };
      }
      if (isIndexer && !drawer.id && body.caps) {
        setIndexerForm((current) => ({
          ...current,
          categories: body.caps?.categories || current.categories,
          scope: (body.caps?.mediaScope as MediaScope | undefined) ?? current.scope
        }));
      }
      if (savedAndUnchanged) revalidator.revalidate();
    } catch (error) {
      setTestResult(abort.signal.aborted
        ? { healthStatus: "unknown", message: "Test cancelled." }
        : { healthStatus: "unreachable", message: error instanceof Error ? error.message : "Test failed." });
    } finally {
      if (testAbort.current === abort) testAbort.current = null;
      setBusy(null);
    }
  }

  function cancelTest() {
    testAbort.current?.abort();
  }

  // The server switches a new indexer or client on in Library Routing (#1135) unless the drawer's
  // switch says not to. This only reads back what it did, to tell the person.
  async function routedNames(kind: "indexer" | "client", id: string): Promise<string[]> {
    const snapshots = await Promise.all(libraries.map((library) =>
      fetchJson<LibraryRoutingSnapshot>(`/api/libraries/${library.id}/routing`).catch((): LibraryRoutingSnapshot => ({ libraryId: library.id, libraryName: library.name, sources: [], downloadClients: [] }))
    ));
    return snapshots
      .filter((snapshot) => kind === "indexer" ? snapshot.sources.some((link) => link.indexerId === id) : snapshot.downloadClients.some((link) => link.downloadClientId === id))
      .map((snapshot) => snapshot.libraryName);
  }
  async function routeNewIndexerNow(created: IndexerItem): Promise<string | null> {
    if (!routeNewIndexer) return null;
    const names = await routedNames("indexer", created.id).catch(() => []);
    return names.length ? `Indexer added and switched on for ${names.join(", ")}` : null;
  }
  async function routeNewClientNow(created: DownloadClientItem): Promise<string | null> {
    if (!routeNewClient) return null;
    const names = await routedNames("client", created.id).catch(() => []);
    return names.length ? `Client added and routed to ${names.join(", ")}` : null;
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    const errors: Record<string, string> =
      drawer.kind === "indexer" ? indexerFormErrors(indexerForm, !drawer.id)
      : drawer.kind === "client" ? clientFormErrors(clientForm)
      : drawer.kind === "proxy" ? proxyFormErrors(proxyForm)
      : {};
    setFieldErrors(errors);
    if (Object.keys(errors).length) return;

    setBusy("save");
    setSaveState("saving");
    try {
      if (drawer.kind === "indexer") {
        const payload = indexerPayload();
        if (drawer.id) {
          await send(`/api/indexers/${drawer.id}`, "PUT", payload, "Indexer could not be saved.");
          const settled = { ...indexerForm, apiKey: "", trackerSecrets: {} };
          setIndexerForm(settled);
          setIndexerInitial(settled);
          setSaveMessage("Saved just now");
        } else {
          const response = await send("/api/indexers", "POST", { ...payload, routeToLibraries: routeNewIndexer, testedHealth: testedNewIndexer.current && sameIndexer(testedNewIndexer.current.form, indexerForm) ? testedNewIndexer.current.health : undefined }, "Indexer could not be added.");
          const created = (await response.json()) as IndexerItem;
          const settled = indexerFormFrom(created);
          setIndexerForm(settled);
          setIndexerInitial(settled);
          setDrawer({ kind: "indexer", id: created.id });
          setSaveMessage(await routeNewIndexerNow(created) ?? "Indexer added");
        }
      } else if (drawer.kind === "client") {
        const payload = clientPayload();
        if (drawer.id) {
          await send(`/api/download-clients/${drawer.id}`, "PUT", buildClientSavePayload(clientForm, clientInitial), "Download client could not be saved.");
          const settled = { ...clientForm, password: "" };
          setClientForm(settled);
          setClientInitial(settled);
          setSaveMessage("Saved just now");
        } else {
          const response = await send("/api/download-clients", "POST", { ...payload, routeToLibraries: routeNewClient }, "Download client could not be added.");
          const created = (await response.json()) as DownloadClientItem;
          const settled = clientFormFrom(created);
          setClientForm(settled);
          setClientInitial(settled);
          setDrawer({ kind: "client", id: created.id });
          setSaveMessage(await routeNewClientNow(created) ?? "Client added");
        }
      } else if (drawer.kind === "proxy") {
        const payload = buildProxySavePayload(proxyForm);
        if (drawer.id) {
          await send(`/api/indexer-proxies/${drawer.id}`, "PUT", payload, "Proxy could not be saved.");
          const settled = { ...proxyForm, password: "" };
          setProxyForm(settled);
          setProxyInitial(settled);
          setSaveMessage("Saved just now");
        } else {
          const response = await send("/api/indexer-proxies", "POST", payload, "Proxy could not be added.");
          const created = (await response.json()) as ProxyItem;
          const settled = proxyFormFrom(created);
          setProxyForm(settled);
          setProxyInitial(settled);
          setDrawer({ kind: "proxy", id: created.id });
          setSaveMessage("Proxy added");
        }
      } else if (drawer.kind === "routing") {
        await send(
          `/api/libraries/${drawer.libraryId}/routing`,
          "PUT",
          {
            sources: routeSources.map((indexerId, index) => ({ indexerId, priority: index + 1, requiredTags: routeSourceTags[indexerId]?.required.trim() ?? "", excludedTags: routeSourceTags[indexerId]?.excluded.trim() ?? "" })),
            downloadClients: routeClients.map((downloadClientId, index) => ({ downloadClientId, priority: index + 1, category: routeCategories[downloadClientId]?.trim() || null }))
          },
          "Routing could not be saved."
        );
        const categories = Object.fromEntries(routeClients.map((id) => [id, routeCategories[id]?.trim() ?? ""]));
        setRouteCategories(categories);
        setRouteInitial({ sources: routeSources, clients: routeClients, categories, sourceTags: routeSourceTags });
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
    } else if (drawer.kind === "proxy" && drawer.id) {
      await run("remove", () => send(`/api/indexer-proxies/${drawer.id}`, "DELETE", undefined, "Proxy could not be removed."), `${editingProxy?.name ?? "Proxy"} removed`);
    } else {
      return;
    }
    setConfirmRemove(false);
    setIndexerInitial(indexerForm);
    setClientInitial(clientForm);
    setProxyInitial(proxyForm);
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
  // #813: dismissing a SABnzbd queue-level warning also clears it at
  // SABnzbd, so it does not reappear next poll.
  async function clearClientWarnings(item: DownloadClientItem) {
    await run(`warnings:clear:${item.id}`, () => send(`/api/download-clients/${item.id}/warnings/clear`, "POST", undefined, `Could not clear ${item.name}'s warnings.`), `Cleared ${item.name}'s warnings`);
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

  function editSharedMapping(mapping: DownloadClientPathMappingItem) {
    setSharedMappingForm({
      editingId: mapping.id,
      anchorClientId: mapping.downloadClientId,
      host: mapping.host ?? "",
      remotePath: mapping.remotePath,
      localPath: mapping.localPath,
      priority: String(mapping.priority)
    });
  }
  function cancelSharedMappingEdit() {
    setSharedMappingForm({ editingId: null, anchorClientId: "", host: "", remotePath: "", localPath: "", priority: "10" });
  }
  async function saveSharedMapping() {
    const form = sharedMappingForm;
    const anchorClientId = form.anchorClientId || clients[0]?.id;
    if (!anchorClientId || !form.remotePath.trim() || !form.localPath.trim()) return;
    const priority = Number(form.priority);
    const payload = {
      remotePath: form.remotePath.trim(),
      localPath: form.localPath.trim(),
      isEnabled: true,
      priority: Number.isFinite(priority) && priority >= 1 ? priority : 10,
      host: form.host.trim() || undefined
    };
    const ok = await run(
      form.editingId ? "shared-mapping:save" : "shared-mapping:add",
      async () => {
        await send(`/api/download-clients/${anchorClientId}/path-mappings`, "POST", payload, "Remote path mapping could not be saved.");
        // Only removed once the replacement exists (see the note by
        // sharedMappingForm's declaration) — an edit that fails to create
        // leaves the original mapping exactly as it was.
        if (form.editingId) {
          const owner = pathMappings.find((item) => item.id === form.editingId)?.downloadClientId ?? form.anchorClientId;
          await send(`/api/download-clients/${owner}/path-mappings/${form.editingId}`, "DELETE", undefined, "The old mapping could not be removed.");
        }
      },
      form.editingId ? "Remote path mapping updated" : "Remote path mapping added"
    );
    if (ok) cancelSharedMappingEdit();
  }
  async function removeSharedMapping(mapping: DownloadClientPathMappingItem) {
    await run(
      `shared-mapping:remove:${mapping.id}`,
      () => send(`/api/download-clients/${mapping.downloadClientId}/path-mappings/${mapping.id}`, "DELETE", undefined, "Remote path mapping could not be removed."),
      "Remote path mapping removed"
    );
    if (sharedMappingForm.editingId === mapping.id) cancelSharedMappingEdit();
  }

  async function checkCategory(clientId: string) {
    const category = routeCategories[clientId]?.trim();
    if (!category) return;
    await run(`category:${clientId}`, async () => {
      const response = await send(
        `/api/download-clients/${clientId}/categories/check`,
        "POST",
        { category },
        "Deluno could not check this download category."
      );
      const result = (await response.json()) as DownloadClientCategoryCheckResult;
      setCategoryChecks((current) => ({ ...current, [clientId]: result }));
      if (result.status === "ready") toast.success(result.message);
      else if (result.status === "missing" || result.status === "unreachable") toast.error(result.message);
    });
  }

  const routingChecks = useRoutingChecks(section === "routing", libraries, routing);
  const readiness = indexerReadiness(indexers, (id) => routing.filter((route) => route.sources.some((source) => source.indexerId === id)).length, libraries.length);

  const toolbarAction =
    section === "indexers" ? (
      <PageToolbarAction onClick={() => openIndexer(null)}>New indexer</PageToolbarAction>
    ) : section === "clients" ? (
      <PageToolbarAction onClick={() => openClient(null)}>New client</PageToolbarAction>
    ) : section === "proxies" ? (
      <PageToolbarAction onClick={() => openProxy(null)}>New proxy</PageToolbarAction>
    ) : null;

  return (
    <div className="grid gap-(--page-gap)">
      <PageToolbar tabs={TABS} actions={toolbarAction} />

      {section === "clients" ? <ClientProgressFeed /> : null}
      {section === "scoreboard" ? <IndexerScoreboard snapshot={scoreboard} /> : null}

      {section === "indexers" ? (
        <ListCard
          title={indexers.length ? `${readiness.ready} of ${readiness.total} ready to search` : "0 indexers"}
          count={indexers.length && readiness.notReady.length ? `Not counted: ${readiness.notReady.join(", ")}` : undefined}
          titleCase={false}
          actions={
            indexers.length > 0 ? (
              <div className="flex min-w-0 items-center gap-2">
                <IndexerFilterChips indexers={indexers} filters={indexerFilters} onChange={setIndexerFilters} />
                <IndexerFilterBar indexers={indexers} filters={indexerFilters} onChange={setIndexerFilters} />
                <Button type="button" variant={selectMode ? "secondary" : "ghost"} size="sm" onClick={toggleSelectMode}>
                  <ListChecks className="h-3.5 w-3.5" />
                  {selectMode ? "Done" : "Select"}
                </Button>
              </div>
            ) : undefined
          }
        >
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
            <>
              {selectMode ? (
                <IndexerBulkBar
                  count={selectedIndexerIds.size}
                  busy={bulkBusy}
                  onEnable={() => void bulkSetEnabled(true)}
                  onPause={() => void bulkSetEnabled(false)}
                  onRemove={() => setConfirmBulkRemove(true)}
                  onChangeScope={(scope) => void bulkSetScope(scope)}
                  onTags={() => setTagsFor("indexer")}
                  onClear={() => setSelectedIndexerIds(new Set())}
                />
              ) : null}
              {visibleIndexers.length === 0 ? (
                <ListEmpty title="No indexers match these filters" description="Clear a filter to see the rest of your indexers." />
              ) : (
                <ListTable
                  listId="indexers"
                  items={visibleIndexers}
                  columns={[
                    { id: "name", label: "Name", sort: (item) => indexerSortValue(item, "name") },
                    ...(selectMode ? [{ id: "select", label: <IndexerSelectAllCheckbox items={visibleIndexers} selected={selectedIndexerIds} onChange={(ids) => setSelectedIndexerIds(new Set(ids))} />, width: "28px", mobile: true, movable: false, sortable: false }] : []),
                    { id: "protocol", label: "Protocol", sort: (item) => indexerSortValue(item, "protocol") },
                    { id: "usedFor", label: "Used for", sort: (item) => indexerSortValue(item, "usedFor") },
                    { id: "lastTest", label: "Last test", sort: (item) => indexerSortValue(item, "lastTest") },
                    { id: "pacing", label: "Pacing", sort: (item) => throttleByHost.get(indexerHost(item.baseUrl) ?? "")?.waiting ?? 0 },
                    { id: "status", label: "Status", sort: (item) => indexerSortValue(item, "status"), width: LIST_TRACK.status, mobile: true },
                    { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (item) => item.isEnabled }
                  ]}
                >
                  {(rows) => rows.map((item) => {
                    const searchedBy = routing.filter((route) => route.sources.some((source) => source.indexerId === item.id)).map((route) => route.libraryName);
                    const chip = unusedChip(item, searchedBy.length, libraries.length) ?? healthChip(item);
                    const throttle = throttleByHost.get(indexerHost(item.baseUrl) ?? "");
                    return (
                      <ListRow key={item.id} onClick={() => openIndexer(item)} selected={drawer.kind === "indexer" && drawer.id === item.id}>
                        <ListNameCell name={item.name} sub={<span className="font-mono">{item.baseUrl}</span>} />
                        {selectMode ? (
                          <ListCell mobile>
                            <Checkbox
                              aria-label={`Select ${item.name}`}
                              checked={selectedIndexerIds.has(item.id)}
                              onClick={(event) => event.stopPropagation()}
                              onCheckedChange={() => toggleIndexerSelected(item.id)}
                            />
                          </ListCell>
                        ) : null}
                        {/* "Private"/"Public" was a label nobody could set, Deluno
                            never read, and two normalisers disagreed about. What a
                            person actually wants to know about a source at a glance
                            is what it expects back — and only when that differs from
                            their normal rule. */}
                        <ListCell primary={protocolLabel(item.protocol)} secondary={sharingRuleFrom(item) === "strict" ? "Strict sharing" : undefined} />
                        <ListCell primary={scopeLabel(item.mediaScope)} secondary={libraries.length === 0 ? `Priority ${item.priority}` : searchedBy.length ? `Searched by ${searchedBy.join(", ")}` : <RoutingFixLink>No library searches it. Switch it on in Library Routing</RoutingFixLink>} />
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
            </>
          )}
        </ListCard>
      ) : null}

      {section === "clients" ? (
        <>
          <DetectedComponents
            category="download-client"
            configuredKinds={clients.map((item) => item.protocol)}
            onConnected={() => revalidator.revalidate()}
            onUse={(status) => openPrefilledClient(status)}
            onNeedsSignIn={(status, result) => openPrefilledClient(status, result.host ?? status.defaultHost, result.port ?? status.defaultPort, false)}
          />
          <ListCard
            title={clients.length ? `${clients.length} ${clients.length === 1 ? "client" : "clients"}` : "0 clients"}
            titleCase={false}
            actions={clients.length > 0 ? <ConnectionSelectButton active={clientSelection.active} onClick={clientSelection.toggleActive} /> : undefined}
          >
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
              <>
              {clientSelection.active ? (
                <ConnectionSelectionBar count={clientSelection.selected.size} noun="download clients" onTags={() => setTagsFor("downloadClient")} onClear={clientSelection.clear} />
              ) : null}
              <ListTable
                listId="download-clients"
                items={clients}
                columns={[
                  { id: "name", label: "Name", sort: (item) => item.name.toLowerCase() },
                  ...(clientSelection.active ? [{ id: "select", label: <ConnectionSelectAll ids={clients.map((item) => item.id)} selected={clientSelection.selected} noun="download clients" onChange={clientSelection.setAll} />, width: "28px", mobile: true, movable: false, sortable: false }] : []),
                  { id: "type", label: "Type", sort: (item) => CLIENT_PRESETS.find((candidate) => candidate.protocol === item.protocol)?.label ?? item.protocol },
                  { id: "categories", label: "Categories", sort: (item) => [item.moviesCategory ?? item.categoryTemplate ?? "", item.tvCategory ?? ""].filter(Boolean).join(" ") },
                  { id: "now", label: "Now", sort: (item) => telemetryByClient.get(item.id)?.summary.activeCount },
                  { id: "status", label: "Status", sort: (item) => { const chip = healthChip(item, telemetryByClient.get(item.id)?.clientPaused ?? false); return toneSortValue(chip.tone, chip.label); }, width: LIST_TRACK.status, mobile: true },
                  { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (item) => item.isEnabled }
                ]}
              >
                {(rows) => rows.map((item) => {
                  const sentBy = routing.filter((route) => route.downloadClients.some((client) => client.downloadClientId === item.id)).map((route) => route.libraryName);
                  const live = telemetryByClient.get(item.id);
                  const chip = unusedChip(item, sentBy.length, libraries.length) ?? healthChip(item, live?.clientPaused ?? false);
                  const preset = CLIENT_PRESETS.find((candidate) => candidate.protocol === item.protocol);
                  const movies = item.moviesCategory ?? item.categoryTemplate ?? "";
                  const tv = item.tvCategory ?? "";
                  const mappings = mappingsByClient.get(item.id) ?? [];
                  return (
                    <ListRow key={item.id} onClick={() => openClient(item)} selected={drawer.kind === "client" && drawer.id === item.id}>
                      <ListNameCell nameTitle={[item.name, connectionVersionLine(versions.get(versionKey("downloadClient", item.id)), live?.clientVersion)].filter(Boolean).join(" ")} name={<>{item.name}<ConnectionVersionBeside info={versions.get(versionKey("downloadClient", item.id))} fallback={live?.clientVersion} /></>} sub={<span className="font-mono">{item.endpointUrl ?? [item.host, item.port].filter(Boolean).join(":")}</span>} />
                      {clientSelection.active ? (
                        <ListCell mobile>
                          <Checkbox
                            aria-label={`Select ${item.name}`}
                            checked={clientSelection.selected.has(item.id)}
                            onClick={(event) => event.stopPropagation()}
                            onCheckedChange={() => clientSelection.toggle(item.id)}
                          />
                        </ListCell>
                      ) : null}
                      <ListCell primary={preset?.label ?? item.protocol} secondary={libraries.length > 0 && sentBy.length === 0 ? <RoutingFixLink>No library sends downloads here. Route it in Library Routing</RoutingFixLink> : sentBy.length ? `Used by ${sentBy.join(", ")}` : preset ? `${preset.kind} · ${preset.authMode}` : undefined} />
                      <ListCell mono primary={[movies, tv].filter(Boolean).join(" · ") || "—"} secondary={movies && tv && movies === tv ? "Same for both — files will mix" : mappings.length ? `${mappings.length} file-location link${mappings.length === 1 ? "" : "s"}` : "Movies · TV"} />
                      <ListCell numeric primary={live ? `${live.summary.activeCount} active` : <span className="text-muted-foreground">—</span>} secondary={live ? `${live.summary.totalSpeedMbps.toFixed(1)} MB/s · ${live.summary.importReadyCount} ready to import` : item.lastHealthTestUtc ? `Tested ${relative(item.lastHealthTestUtc)}` : "Not tested"} />
                      <ListCell mobile className="max-md:max-w-[7.5rem]">
                        <div className="flex flex-wrap items-center gap-1.5">
                          <Chip tone={chip.tone}>{chip.label}</Chip>
                          {/* #813: a SABnzbd queue-level warning, cleared at SABnzbd (not just hidden here) so it does not reappear next poll. */}
                          {live?.clientWarnings?.length ? (
                            <Button
                              type="button"
                              size="sm"
                              variant="ghost"
                              aria-label={`Clear ${item.name}'s warnings`}
                              title={live.clientWarnings.join(" ")}
                              disabled={busy === `warnings:clear:${item.id}`}
                              onClick={(event) => { event.stopPropagation(); void clearClientWarnings(item); }}
                            >
                              {busy === `warnings:clear:${item.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : "Clear warnings"}
                            </Button>
                          ) : null}
                        </div>
                      </ListCell>
                      <ListCell mobile>
                        <Switch size="sm" aria-label={`${item.isEnabled ? "Pause" : "Enable"} ${item.name}`} checked={item.isEnabled} disabled={busy === `toggle:${item.id}`} onCheckedChange={(checked) => void toggleClient(item, checked)} />
                      </ListCell>
                    </ListRow>
                  );
                })}
              </ListTable>
              </>
            )}
          </ListCard>
        </>
      ) : null}

      {section === "pathMappings" ? (
        <>
          <ListCard
            title={pathMappings.length ? `${pathMappings.length} ${pathMappings.length === 1 ? "mapping" : "mappings"}` : "0 mappings"}
            titleCase={false}
          >
            <p className="px-(--card-pad-x) pb-3 text-(length:--type-caption) text-muted-foreground">
              Only needed when a download client reports a path Deluno cannot use directly — for example a container reports <code className="font-mono">/downloads/complete</code> while Deluno reads <code className="font-mono">D:\Downloads\complete</code>. A mapping applies to every download client at its host, so it only needs adding once per host, however many clients share it.
            </p>
            {clients.length === 0 ? (
              <ListEmpty title="No download client yet" description="Add a download client first, then a remote path mapping can be filed under its host." />
            ) : pathMappings.length === 0 ? (
              <ListEmpty title="No remote path mappings yet" description="Add one below if a client's reported path does not match what Deluno sees." />
            ) : (
              <ListTable
                listId="path-mappings"
                items={pathMappings}
                columns={[
                  { id: "host", label: "Host", sort: (mapping) => (mapping.host ?? "").toLowerCase() },
                  { id: "remote", label: "Client path", sort: (mapping) => mapping.remotePath.toLowerCase() },
                  { id: "local", label: "Deluno path", sort: (mapping) => mapping.localPath.toLowerCase() },
                  { id: "applies", label: "Applies to", sort: (mapping) => clientsForMapping(mapping).map((client) => client.name).join(", ").toLowerCase() },
                  { id: "actions", label: "", width: LIST_TRACK.status }
                ]}
              >
                {(rows) => rows.map((mapping) => {
                  const appliesTo = clientsForMapping(mapping);
                  return (
                    <ListRow key={mapping.id}>
                      <ListCell mono primary={mapping.host ?? "—"} />
                      <ListCell mono primary={mapping.remotePath} />
                      <ListCell mono primary={mapping.localPath} />
                      <ListCell primary={appliesTo.length ? appliesTo.map((client) => client.name).join(", ") : "No client at this host yet"} />
                      <ListCell>
                        <div className="flex items-center gap-1.5">
                          <Button type="button" variant="outline" size="sm" className="h-7 px-2" onClick={() => testMapping(mapping.downloadClientId, mapping.id)} disabled={busy !== null}>
                            {busy === `mapping:test:${mapping.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : "Test"}
                          </Button>
                          <Button type="button" variant="outline" size="sm" className="h-7 px-2" onClick={() => editSharedMapping(mapping)} disabled={busy !== null}>
                            Edit
                          </Button>
                          <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Remove the ${mapping.host ?? mapping.remotePath} mapping`} onClick={() => void removeSharedMapping(mapping)} disabled={busy !== null}>
                            {busy === `shared-mapping:remove:${mapping.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : "×"}
                          </Button>
                        </div>
                      </ListCell>
                    </ListRow>
                  );
                })}
              </ListTable>
            )}
            {clients.length > 0 ? (
              <div className="flex flex-col gap-2 border-t border-hairline px-(--card-pad-x) py-3">
                <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-5">
                  <Select
                    aria-label="Download client this mapping is filed under"
                    value={sharedMappingForm.anchorClientId || clients[0]?.id || ""}
                    onChange={(event) => setSharedMappingForm({ ...sharedMappingForm, anchorClientId: event.target.value })}
                    options={clients.map((client) => ({ value: client.id, label: client.name }))}
                  />
                  <Input
                    aria-label="Host (leave blank to use the selected client's own host)"
                    value={sharedMappingForm.host}
                    onChange={(event) => setSharedMappingForm({ ...sharedMappingForm, host: event.target.value })}
                    placeholder="Host (defaults to the client's own)"
                    className="font-mono text-(length:--type-caption)"
                  />
                  <Input
                    aria-label="Client path"
                    value={sharedMappingForm.remotePath}
                    onChange={(event) => setSharedMappingForm({ ...sharedMappingForm, remotePath: event.target.value })}
                    placeholder="/downloads/complete"
                    className="font-mono text-(length:--type-caption)"
                  />
                  <Input
                    aria-label="Deluno path"
                    value={sharedMappingForm.localPath}
                    onChange={(event) => setSharedMappingForm({ ...sharedMappingForm, localPath: event.target.value })}
                    placeholder="D:\Downloads\complete"
                    className="font-mono text-(length:--type-caption)"
                  />
                  <div className="flex items-center gap-2">
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="w-max"
                      onClick={() => void saveSharedMapping()}
                      disabled={busy !== null || !sharedMappingForm.remotePath.trim() || !sharedMappingForm.localPath.trim()}
                    >
                      {busy === "shared-mapping:add" || busy === "shared-mapping:save" ? (
                        <Loader2 className="h-3.5 w-3.5 animate-spin" />
                      ) : sharedMappingForm.editingId ? null : (
                        <Plus className="h-3.5 w-3.5" />
                      )}
                      {sharedMappingForm.editingId ? "Save changes" : "Add mapping"}
                    </Button>
                    {sharedMappingForm.editingId ? (
                      <Button type="button" variant="ghost" size="sm" onClick={cancelSharedMappingEdit} disabled={busy !== null}>
                        Cancel
                      </Button>
                    ) : null}
                  </div>
                </div>
              </div>
            ) : null}
          </ListCard>
        </>
      ) : null}

      {section === "routing" ? (
        <>
          {/* The routing explainer that used to sit here said what the routing
              drawer already says beside the field itself, in more detail and in
              the place you actually need it. One subject, one home. */}
          <ListCard title={libraries.length ? `${libraries.length} ${libraries.length === 1 ? "library" : "libraries"}` : "0 libraries"} titleCase={false}>
          {libraries.length === 0 ? (
            <ListEmpty title="No libraries yet" description="Create a Movies or TV library first, then choose its search and download connections here." />
          ) : (
            <ListTable
              listId="library-routing"
              items={libraries}
              columns={[
                { id: "name", label: "Name", sort: (library) => library.name.toLowerCase() },
                { id: "indexers", label: "Indexers", sort: (library) => routing.find((item) => item.libraryId === library.id)?.sources.length ?? 0 },
                { id: "clients", label: "Download clients", sort: (library) => routing.find((item) => item.libraryId === library.id)?.downloadClients.length ?? 0 },
                { id: "status", label: "Status", width: LIST_TRACK.status, mobile: true, sort: (library) => { const snapshot = routing.find((item) => item.libraryId === library.id); return !snapshot?.sources.length ? "0-no indexers" : !snapshot.downloadClients.length ? "1-no client" : "2-ready"; } }
              ]}
            >
              {(rows) => rows.map((library) => {
                const snapshot = routing.find((item) => item.libraryId === library.id);
                const sources = snapshot?.sources ?? [];
                const targets = snapshot?.downloadClients ?? [];
                const status = routingStatus(snapshot, routingChecks[library.id]);
                return (
                  <ListRow key={library.id} onClick={() => openRouting(library)} selected={drawer.kind === "routing" && drawer.libraryId === library.id}>
                    <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                    <ListCell numeric primary={sources.length ? `${sources.length} of ${indexers.length}` : <span className="text-muted-foreground">None</span>} secondary={sources.map((source) => source.indexerName).join(", ") || "Deluno can't search for this library"} />
                    <ListCell numeric primary={targets.length ? `${targets.length} of ${clients.length}` : <span className="text-muted-foreground">None</span>} secondary={targets.map((target) => `${target.downloadClientName}${target.category?.trim() ? ` · ${target.category.trim()}` : ""}`).join(", ") || "Nowhere to send releases"} />
                    <ListCell mobile>
                      <span className="flex flex-wrap items-center gap-2">
                        <Chip tone={status.tone}>{status.label}</Chip>
                        {status.needsFix ? (
                          <Button type="button" size="sm" variant="outline" aria-label={`Fix ${library.name} routing`} title={status.hint} onClick={(event) => { event.stopPropagation(); openRouting(library); }}>
                            Fix
                          </Button>
                        ) : null}
                      </span>
                      {status.hint ? <span className="mt-1 block max-w-prose text-(length:--type-caption) text-muted-foreground">{status.hint}</span> : null}
                    </ListCell>
                  </ListRow>
                );
              })}
            </ListTable>
          )}
          </ListCard>
        </>
      ) : null}

      {section === "proxies" ? (
        <ListCard title={proxies.length ? `${proxies.length} ${proxies.length === 1 ? "proxy" : "proxies"}` : "0 proxies"} titleCase={false}>
          {proxies.length === 0 ? (
            <ListEmpty
              title="No proxies yet"
              description="Add an HTTP, SOCKS4 or SOCKS5 proxy, then assign it to whichever indexers need it from that indexer's own drawer. A Cloudflare helper for every tracker is set once in Preferences, General; a FlareSolverr / Byparr proxy here is only for one indexer that needs its own, and it wins over that one."
              actions={
                <Button type="button" size="sm" onClick={() => openProxy(null)}>
                  <Plus className="h-3.5 w-3.5" />
                  New proxy
                </Button>
              }
            />
          ) : (
            <ListTable
              listId="proxies"
              items={proxies}
              columns={[
                { id: "name", label: "Name", sort: (item) => item.name.toLowerCase() },
                { id: "type", label: "Type", sort: (item) => PROXY_TYPE_LABELS[item.proxyType] ?? item.proxyType },
                { id: "usedBy", label: "Used by", sort: (item) => item.assignedIndexerNames.length },
                { id: "on", label: "On", width: LIST_TRACK.toggle, mobile: true, sort: (item) => item.isEnabled }
              ]}
            >
              {(rows) => rows.map((item) => (
                <ListRow key={item.id} onClick={() => openProxy(item)} selected={drawer.kind === "proxy" && drawer.id === item.id}>
                  <ListNameCell name={item.name} sub={<span className="font-mono">{item.address}</span>} />
                  <ListCell primary={PROXY_TYPE_LABELS[item.proxyType] ?? item.proxyType} />
                  <ListCell primary={item.assignedIndexerNames.length ? `${item.assignedIndexerNames.length} indexer${item.assignedIndexerNames.length === 1 ? "" : "s"}` : <span className="text-muted-foreground">None</span>} secondary={item.assignedIndexerNames.join(", ") || undefined} />
                  <ListCell mobile>
                    <Chip tone={item.isEnabled ? "ok" : "idle"}>{item.isEnabled ? "On" : "Off"}</Chip>
                  </ListCell>
                </ListRow>
              ))}
            </ListTable>
          )}
        </ListCard>
      ) : null}

      <Drawer
        open={drawer.kind !== "closed"}
        onOpenChange={(open) => {
          if (!open) requestClose();
        }}
        title={
          drawer.kind === "indexer" ? editingIndexer?.name ?? (indexerForm.name || "New indexer")
          : drawer.kind === "client" ? editingClient?.name ?? (clientForm.name || "New download client")
          : drawer.kind === "proxy" ? editingProxy?.name ?? "New proxy"
          : drawer.kind === "routing" ? routingLibrary?.name ?? "Library routing"
          : ""
        }
        description={
          drawer.kind === "indexer" ? (editingIndexer ? `${protocolLabel(editingIndexer.protocol)} indexer · used for ${scopeLabel(editingIndexer.mediaScope)}` : "Where Deluno looks for releases.")
          : drawer.kind === "client" ? (editingClient ? `${protocolLabel(editingClient.protocol)} · ${editingClient.endpointUrl ?? ""}` : "Where Deluno sends approved releases.")
          : drawer.kind === "proxy" ? (editingProxy ? `${PROXY_TYPE_LABELS[editingProxy.proxyType] ?? editingProxy.proxyType} · ${editingProxy.address}` : "Where an indexer's requests are sent instead of directly.")
          : drawer.kind === "routing" ? `Choose where ${routingLibrary?.name ?? "this library"} searches and where its downloads go`
          : undefined
        }
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter
            state={footerState}
            message={saveMessage}
            saveLabel={drawer.kind === "routing" ? "Save routing" : drawer.kind === "client" ? (drawer.id ? "Save client" : "Add client") : drawer.kind === "proxy" ? (drawer.id ? "Save proxy" : "Add proxy") : drawer.kind === "indexer" && drawer.id ? "Save indexer" : "Add indexer"}
            onCancel={requestClose}
            extraAction={drawer.kind === "indexer" || drawer.kind === "client" ? (
              busy === "test:form" ? (
                <Button type="button" variant="outline" onClick={cancelTest}>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Cancel test
                </Button>
              ) : (
                <Button type="button" variant="outline" onClick={() => void testForm()} disabled={busy !== null}>
                  <Wifi className="h-4 w-4" />
                  Test
                </Button>
              )
            ) : undefined}
            saveEnabled={creating ? true : undefined}
            disabled={busy !== null}
          />
        }
      >
        {(drawer.kind === "indexer" || drawer.kind === "client") && testResult ? <ConnectionTestResult result={testResult} /> : null}
        {drawer.kind === "indexer" ? (
          <IndexerDrawerBody
            form={indexerForm}
            setForm={setIndexerForm}
            settings={settings}
            proxies={proxies}
            editing={editingIndexer}
            throttle={editingIndexerThrottle}
            errors={fieldErrors}
            clearError={(key) => setFieldErrors((current) => ({ ...current, [key]: "" }))}
            showKey={showKey}
            setShowKey={setShowKey}
            fineTuneOpen={fineTuneOpen}
            setFineTuneOpen={setFineTuneOpen}
            busy={busy}
            onReset={() => editingIndexer && void run(`reset:${editingIndexer.id}`, () => send(`/api/indexers/${editingIndexer.id}/reset-circuit`, "POST", undefined, "Could not reset."), `Circuit reset for ${editingIndexer.name}`)}
            onAcknowledgeVip={() => editingIndexer && void run(`acknowledge-vip:${editingIndexer.id}`, () => send(`/api/indexers/${editingIndexer.id}`, "PUT", { acknowledgeVipExpiry: true }, "Could not acknowledge."), `VIP warning acknowledged for ${editingIndexer.name}`)}
            onRemove={() => setConfirmRemove(true)}
            testMessage={testResult?.message ?? null}
            routeOnAdd={routeNewIndexer}
            onRouteOnAddChange={setRouteNewIndexer}
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
            onRemove={() => setConfirmRemove(true)}
            blocklistSync={editingClient ? qbittorrentBlocklistSync[editingClient.id] ?? null : null}
            routeOnAdd={routeNewClient}
            onRouteOnAddChange={setRouteNewClient}
            warnings={editingClient ? telemetryByClient.get(editingClient.id)?.clientWarnings ?? undefined : undefined}
            onClearWarnings={() => editingClient && void clearClientWarnings(editingClient)}
            versionLine={editingClient ? connectionVersionLine(versions.get(versionKey("downloadClient", editingClient.id)), telemetryByClient.get(editingClient.id)?.clientVersion) : undefined}
          />
        ) : null}
        {drawer.kind === "proxy" ? (
          <ProxyDrawerBody
            form={proxyForm}
            setForm={setProxyForm}
            editing={editingProxy}
            errors={fieldErrors}
            clearError={(key) => setFieldErrors((current) => ({ ...current, [key]: "" }))}
            busy={busy}
            onRemove={() => setConfirmRemove(true)}
          />
        ) : null}
        {drawer.kind === "routing" && routingLibrary ? (
          <RoutingDrawerBody
            library={routingLibrary}
            folderCheckKey={JSON.stringify(routeInitial)}
            indexers={indexers}
            clients={clients}
            sources={routeSources}
            targets={routeClients}
            categories={routeCategories}
            sourceTags={routeSourceTags}
            onSourceTagsChange={(id, patch) => setRouteSourceTags((current) => ({ ...current, [id]: { required: current[id]?.required ?? "", excluded: current[id]?.excluded ?? "", ...patch } }))}
            onToggleSource={(id, on) => setRouteSources((current) => (on ? [...new Set([...current, id])] : current.filter((item) => item !== id)))}
            onToggleClient={(id, on) => {
              setRouteClients((current) => (on ? [...new Set([...current, id])] : current.filter((item) => item !== id)));
              if (!on) {
                setRouteCategories((current) => {
                  const next = { ...current };
                  delete next[id];
                  return next;
                });
                setCategoryChecks((current) => {
                  const next = { ...current };
                  delete next[id];
                  return next;
                });
              }
            }}
            onCategoryChange={(id, value) => {
              setRouteCategories((current) => ({ ...current, [id]: value }));
              setCategoryChecks((current) => {
                const next = { ...current };
                delete next[id];
                return next;
              });
            }}
            categoryChecks={categoryChecks}
            onCheckCategory={(id) => void checkCategory(id)}
            busy={busy}
          />
        ) : null}
      </Drawer>

      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={`Remove “${drawer.kind === "indexer" ? editingIndexer?.name : drawer.kind === "proxy" ? editingProxy?.name : editingClient?.name}”?`}
        description={
          drawer.kind === "indexer" ? "Libraries routed only to this indexer will need another one before Deluno can search for them."
          : drawer.kind === "proxy" ? "Any indexer assigned to it goes back to a direct connection."
          : "Downloads already in this client are left alone; Deluno just stops sending new ones there."
        }
        confirmLabel={drawer.kind === "indexer" ? "Remove indexer" : drawer.kind === "proxy" ? "Remove proxy" : "Remove client"}
        busy={busy === "remove"}
        onConfirm={() => void handleRemove()}
      />
      <ConnectionTagsDialog
        open={tagsFor !== null}
        kind={tagsFor ?? "indexer"}
        noun={tagsFor === "downloadClient" ? "download clients" : "indexers"}
        ids={[...(tagsFor === "downloadClient" ? clientSelection.selected : selectedIndexerIds)]}
        onOpenChange={(open) => { if (!open) setTagsFor(null); }}
        onApplied={() => {
          if (tagsFor === "downloadClient") clientSelection.clear();
          else setSelectedIndexerIds(new Set());
          revalidator.revalidate();
        }}
      />
      <ConfirmDialog
        open={confirmBulkRemove}
        onOpenChange={setConfirmBulkRemove}
        title={`Remove ${selectedIndexerIds.size} indexer${selectedIndexerIds.size === 1 ? "" : "s"}?`}
        description="Libraries routed only to one of these indexers will need another one before Deluno can search for them."
        confirmLabel="Remove indexers"
        busy={bulkBusy}
        onConfirm={() => void bulkRemove()}
      />
      <ConfirmDialog
        open={confirmDiscard}
        onOpenChange={(open) => {
          if (open) return;
          setConfirmDiscard(false);
        }}
        title="Discard unsaved changes?"
        description="Your edits haven't been saved."
        confirmLabel="Discard"
        onConfirm={() => {
          setConfirmDiscard(false);
          closeDrawer();
        }}
      />
    </div>
  );
}

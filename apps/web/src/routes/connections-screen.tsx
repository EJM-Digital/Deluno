import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { useLoaderData, useLocation, useNavigate, useRevalidator } from "react-router-dom";
import { Plus } from "lucide-react";
import {
  fetchJson,
  readValidationProblem,
  type DownloadClientItem,
  type DownloadClientCategoryCheckResult,
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
import type { PlatformSettingsPatch } from "../lib/api/settings";
import { useApiMutation } from "../lib/use-api-mutation";
import { authedFetch } from "../lib/use-auth";
import { RealtimeGroups, useSignalREvent } from "../lib/use-signalr";
import { configurationNavAreas } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Chip } from "../components/ui/chip";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Drawer, DrawerFooter, type DrawerSaveState } from "../components/ui/drawer";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar, PageToolbarAction } from "../components/ui/page-toolbar";
import { Switch } from "../components/ui/switch";
import { toast } from "../components/shell/toaster";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { CLIENT_PRESETS } from "./connections/presets";
import { STRICT_SHARING, clientFormErrors, clientFormFrom, emptyClientForm, emptyIndexerForm, indexerFormErrors, indexerFormFrom, sameClient, sameIndexer, sameSet, sharingRuleFrom, type ClientForm, type DrawerState, type IndexerForm, type Section } from "./connections/forms";
import { formatSeconds, healthChip, indexerHost, protocolLabel, relative, scopeLabel } from "./connections/format";
import { ClientDrawerBody } from "./connections/client-drawer-body";
import { IndexerDrawerBody } from "./connections/indexer-drawer-body";
import { RoutingDrawerBody } from "./connections/routing-drawer-body";

const TABS = configurationNavAreas.find((area) => area.to === "/indexers/indexers")?.items ?? [];

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

type RouteCategories = Record<string, string>;

function sameRouteCategories(left: RouteCategories, right: RouteCategories) {
  const keys = new Set([...Object.keys(left), ...Object.keys(right)]);
  return [...keys].every((key) => (left[key] ?? "") === (right[key] ?? ""));
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

async function send(url: string, method: string, body?: unknown, failure = "Request failed.") {
  const response = await authedFetch(url, { method, headers: body === undefined ? undefined : { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok && response.status !== 204) {
    const problem = await readValidationProblem(response.clone()).catch(() => null);
    const detail = problem?.errors ? Object.values(problem.errors).flat()[0] : problem?.title;
    throw new Error(detail || failure);
  }
  return response;
}

export function IndexersPage() {
  const { clients, pathMappings, indexers, libraries, routing, settings, telemetry, outboundThrottle } = useLoaderData() as LoaderData;
  const location = useLocation();
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const settingsMutation = useApiMutation<PlatformSettingsPatch, PlatformSettingsSnapshot>("/api/settings", "PATCH");
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

  useSignalREvent("DownloadProgress", RealtimeGroups.Queue, () => {
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

  const [drawer, setDrawer] = useState<DrawerState>({ kind: "closed" });
  const [indexerForm, setIndexerForm] = useState<IndexerForm>(emptyIndexerForm);
  const [indexerInitial, setIndexerInitial] = useState<IndexerForm>(emptyIndexerForm);
  const [clientForm, setClientForm] = useState<ClientForm>(emptyClientForm);
  const [clientInitial, setClientInitial] = useState<ClientForm>(emptyClientForm);
  const [routeSources, setRouteSources] = useState<string[]>([]);
  const [routeClients, setRouteClients] = useState<string[]>([]);
  const [routeCategories, setRouteCategories] = useState<RouteCategories>({});
  const [categoryChecks, setCategoryChecks] = useState<Record<string, DownloadClientCategoryCheckResult>>({});
  const [routeInitial, setRouteInitial] = useState<{ sources: string[]; clients: string[]; categories: RouteCategories }>({ sources: [], clients: [], categories: {} });
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
    if (drawer.kind === "routing") return !sameSet(routeSources, routeInitial.sources) || !sameSet(routeClients, routeInitial.clients) || !sameRouteCategories(routeCategories, routeInitial.categories);
    return false;
  }, [drawer.kind, indexerForm, indexerInitial, clientForm, clientInitial, routeSources, routeClients, routeCategories, routeInitial]);
  const footerState: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  /**
   * A create drawer opens with values the user is entitled to accept as-is, so
   * its Save is gated on the form being *valid*, not on it being *dirty* (#293).
   * "Unchanged from the defaults" is not "nothing to save"; gating on dirty made
   * Add client an inert control on the one path a new user actually takes.
   * Submit runs the same field validation either way, so an incomplete create
   * form now says what is missing instead of doing nothing. Editing keeps the
   * dirty gate, where Save with no changes really is a no-op.
   */
  const creating = (drawer.kind === "indexer" || drawer.kind === "client") && drawer.id === null;
  useUnsavedChanges(dirty);

  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
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
  const openRouting = useCallback((library: LibraryItem) => {
    const snapshot = routing.find((item) => item.libraryId === library.id);
    const sources = snapshot?.sources.map((source) => source.indexerId) ?? [];
    const clientIds = snapshot?.downloadClients.map((client) => client.downloadClientId) ?? [];
    const categories = Object.fromEntries(snapshot?.downloadClients.map((client) => [client.downloadClientId, client.category ?? ""]) ?? []);
    setDrawer({ kind: "routing", libraryId: library.id });
    setRouteSources(sources);
    setRouteClients(clientIds);
    setRouteCategories(categories);
    setCategoryChecks({});
    setRouteInitial({ sources, clients: clientIds, categories });
    resetDrawerChrome();
  }, [resetDrawerChrome, routing]);

  useEffect(() => {
    if (section !== "routing" || drawer.kind !== "closed") return;
    const libraryId = new URLSearchParams(location.search).get("libraryId");
    if (!libraryId) return;
    const library = libraries.find((item) => item.id === libraryId);
    if (!library) return;
    openRouting(library);
    navigate("/connections/library-routing", { replace: true });
  }, [drawer.kind, libraries, location.search, navigate, openRouting, section]);
  function closeDrawer() {
    setDrawer({ kind: "closed" });
    setConfirmDiscard(false);
  }
  function requestClose() {
    if (dirty) setConfirmDiscard(true);
    else closeDrawer();
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    const errors: Record<string, string> =
      drawer.kind === "indexer" ? indexerFormErrors(indexerForm, !drawer.id)
      : drawer.kind === "client" ? clientFormErrors(clientForm)
      : {};
    setFieldErrors(errors);
    if (Object.keys(errors).length) return;

    setBusy("save");
    setSaveState("saving");
    try {
      if (drawer.kind === "indexer") {
        const payload = {
          name: indexerForm.name.trim(),
          protocol: indexerForm.protocol,
          // Privacy is not sent at all. Deluno branches on none of it, nobody
          // can set it here, and guessing "private" for every hand-added source
          // was inventing provenance. It stays a fact a migration imports from
          // the app you came from, where it seeds the sharing rule that does
          // change behaviour.
          baseUrl: indexerForm.baseUrl.trim(),
          apiKey: indexerForm.apiKey.trim() || undefined,
          priority: Number(indexerForm.priority || 10),
          requestIntervalSeconds: indexerForm.requestIntervalSeconds.trim() ? Number(indexerForm.requestIntervalSeconds) : null,
          clearRequestInterval: Boolean(drawer.id) && !indexerForm.requestIntervalSeconds.trim(),
          categories: indexerForm.categories,
          tags: "",
          mediaScope: indexerForm.scope,
          isEnabled: indexerForm.isEnabled,
          // Only written when the user actually answered the question
          // differently. A source may carry a hand-tuned rule this one control
          // cannot express, and saving an unrelated field should not flatten it
          // back to the canned one (#288).
          ...(indexerForm.sharingRule === indexerInitial.sharingRule
            ? {}
            : indexerForm.sharingRule === "strict"
              ? {
                  sharingMode: "share-then-tidy",
                  sharingForHours: STRICT_SHARING.forHours,
                  sharingUntilRatio: STRICT_SHARING.untilRatio,
                  sharingStuckAction: "keep-waiting",
                  sharingStuckAfterDays: STRICT_SHARING.stuckAfterDays,
                  clearSharingPolicy: false
                }
              : { clearSharingPolicy: true })
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
            downloadClients: routeClients.map((downloadClientId, index) => ({ downloadClientId, priority: index + 1, category: routeCategories[downloadClientId]?.trim() || null }))
          },
          "Routing could not be saved."
        );
        const categories = Object.fromEntries(routeClients.map((id) => [id, routeCategories[id]?.trim() ?? ""]));
        setRouteCategories(categories);
        setRouteInitial({ sources: routeSources, clients: routeClients, categories });
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
    await run("permission", () => settingsMutation.mutate({ removeCompletedDownloads: allowed }));
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

  const enabledIndexers = indexers.filter((item) => item.isEnabled).length;
  const healthyIndexers = indexers.filter((item) => item.isEnabled && item.healthStatus === "healthy").length;

  const toolbarAction =
    section === "indexers" ? (
      <PageToolbarAction onClick={() => openIndexer(null)}>New indexer</PageToolbarAction>
    ) : section === "clients" ? (
      <PageToolbarAction onClick={() => openClient(null)}>New client</PageToolbarAction>
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
                    <ListNameCell name={item.name} sub={<span className="font-mono">{item.baseUrl}</span>} />
                    {/* "Private"/"Public" was a label nobody could set, Deluno
                        never read, and two normalisers disagreed about. What a
                        person actually wants to know about a source at a glance
                        is what it expects back — and only when that differs from
                        their normal rule. */}
                    <ListCell primary={protocolLabel(item.protocol)} secondary={sharingRuleFrom(item) === "strict" ? "Strict sharing" : undefined} />
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
                      <ListNameCell name={item.name} sub={<span className="font-mono">{item.endpointUrl ?? [item.host, item.port].filter(Boolean).join(":")}</span>} />
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
              <ListNameCell name="Remove items from the client queue" sub="A confirmed, manual Remove on Transfers. Automatic cleanup is configured in Automation & Recovery." />
                <ListCell primary="All download clients" secondary="SABnzbd, qBittorrent, …" />
                <ListCell mobile>
                  <Chip tone={settings.removeCompletedDownloads ? "ok" : "idle"}>{settings.removeCompletedDownloads ? "Allowed" : "Blocked"}</Chip>
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
        <>
          {/* The routing explainer that used to sit here said what the routing
              drawer already says beside the field itself, in more detail and in
              the place you actually need it. One subject, one home. */}
          <ListCard title="Library routing" count={libraries.length ? `${libraries.length} ${libraries.length === 1 ? "library" : "libraries"}` : undefined}>
          {libraries.length === 0 ? (
            <ListEmpty title="No libraries yet" description="Create a Movies or TV library first, then choose its search and download connections here." />
          ) : (
            <ListTable columns={[{ label: "Name" }, { label: "Indexers" }, { label: "Download clients" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]}>
              {libraries.map((library) => {
                const snapshot = routing.find((item) => item.libraryId === library.id);
                const sources = snapshot?.sources ?? [];
                const targets = snapshot?.downloadClients ?? [];
                const status = !sources.length
                  ? { tone: "warn" as const, label: "No indexers" }
                  : !targets.length
                    ? { tone: "warn" as const, label: "No client" }
                    : targets.some((target) => Boolean(target.category?.trim()))
                      ? { tone: "info" as const, label: "Category route" }
                      : { tone: "ok" as const, label: "Ready" };
                return (
                  <ListRow key={library.id} onClick={() => openRouting(library)} selected={drawer.kind === "routing" && drawer.libraryId === library.id}>
                    <ListNameCell name={library.name} sub={library.mediaType === "tv" ? "TV shows" : "Movies"} />
                    <ListCell numeric primary={sources.length ? `${sources.length} of ${indexers.length}` : <span className="text-muted-foreground">None</span>} secondary={sources.map((source) => source.indexerName).join(", ") || "Deluno can't search for this library"} />
                    <ListCell numeric primary={targets.length ? `${targets.length} of ${clients.length}` : <span className="text-muted-foreground">None</span>} secondary={targets.map((target) => `${target.downloadClientName}${target.category?.trim() ? ` · ${target.category.trim()}` : ""}`).join(", ") || "Nowhere to send releases"} />
                    <ListCell mobile>
                      <Chip tone={status.tone}>{status.label}</Chip>
                    </ListCell>
                  </ListRow>
                );
              })}
            </ListTable>
          )}
          </ListCard>
        </>
      ) : null}

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
          : drawer.kind === "routing" ? `Choose where ${routingLibrary?.name ?? "this library"} searches and where its downloads go`
          : undefined
        }
        onSubmit={handleSubmit}
        footer={
          <DrawerFooter
            state={footerState}
            message={saveMessage}
            saveLabel={drawer.kind === "routing" ? "Save routing" : drawer.kind === "client" ? (drawer.id ? "Save client" : "Add client") : drawer.kind === "indexer" && drawer.id ? "Save indexer" : "Add indexer"}
            onCancel={requestClose}
            saveEnabled={creating ? true : undefined}
            disabled={busy !== null}
          />
        }
      >
        {drawer.kind === "indexer" ? (
          <IndexerDrawerBody
            form={indexerForm}
            setForm={setIndexerForm}
            settings={settings}
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
            categories={routeCategories}
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
        title={`Remove “${drawer.kind === "indexer" ? editingIndexer?.name : editingClient?.name}”?`}
        description={drawer.kind === "indexer" ? "Libraries routed only to this indexer will need another one before Deluno can search for them." : "Downloads already in this client are left alone; Deluno just stops sending new ones there."}
        confirmLabel={drawer.kind === "indexer" ? "Remove indexer" : "Remove client"}
        busy={busy === "remove"}
        onConfirm={() => void handleRemove()}
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

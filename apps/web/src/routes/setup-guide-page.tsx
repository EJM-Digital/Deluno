import { type FormEvent, type ReactNode, useEffect, useMemo, useRef, useState } from "react";
import { Link, useLoaderData, useNavigate, useSearchParams } from "react-router-dom";
import {
  ArrowLeft,
  ArrowRight,
  CheckCircle2,
  DownloadCloud,
  FolderTree,
  Loader2,
  Plus,
  RadioTower,
  Rocket,
  Search,
  Settings2,
  ShieldCheck,
  Sparkles,
  Wifi,
  type LucideIcon
} from "lucide-react";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  type ConnectionTestResponse,
  type DownloadClientItem,
  type IndexerItem,
  type LibraryItem,
  type MetadataProviderStatus,
  type MetadataSearchResult,
  type PlatformSettingsSnapshot,
  type SetupProgressItem,
  type SetupDraftItem,
  type QualityProfileItem,
  type CustomFormatItem
} from "../lib/api";
import {
  CUSTOM_FORMAT_BUNDLES,
  findBundledCF,
  type BundledCF,
  type CustomFormatBundle
} from "../lib/trash-guide-data";
import { authedFetch } from "../lib/use-auth";
import { cn } from "../lib/utils";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { Checkbox } from "../components/ui/checkbox";
import { Field } from "../components/ui/field";
import { PathInput } from "../components/ui/path-input";
import { Select } from "../components/ui/select";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Badge } from "../components/ui/badge";
import { Chip } from "../components/ui/chip";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { SummaryStrip } from "../components/ui/summary-strip";

type StepId = "mode" | "folders" | "quality" | "services" | "finish";

interface SetupGuideLoaderData {
  settings: PlatformSettingsSnapshot;
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  indexers: IndexerItem[];
  clients: DownloadClientItem[];
  metadataStatus: MetadataProviderStatus | null;
  progress: SetupProgressItem;
  draft: SetupDraftItem;
}

interface GuideForm {
  mode: "simple" | "advanced";
  mediaIntent: "movies" | "tv" | "both";
  movieRootPath: string;
  seriesRootPath: string;
  qualityPreset: "" | "balanced1080p" | "premium4k";
  formatGoal: "" | "simpleClean" | "balanced" | "homeTheater" | "storageSaver";
  indexerName: string;
  indexerProtocol: "torznab" | "newznab" | "rss";
  indexerUrl: string;
  indexerApiKey: string;
  clientName: string;
  clientProtocol: "qbittorrent" | "sabnzbd" | "transmission" | "deluge" | "nzbget" | "utorrent";
  clientHost: string;
  clientPort: string;
  clientUsername: string;
  clientPassword: string;
  backupEnabled: boolean;
  firstTitleType: "movies" | "tv";
  firstTitle: string;
  firstTitleYear: string;
  firstTitleMonitored: boolean;
  firstTitleMetadata: MetadataSearchResult | null;
}

type ServiceTestState = {
  indexer: "idle" | "testing" | "passed" | "failed";
  client: "idle" | "testing" | "passed" | "failed";
  indexerMessage: string | null;
  clientMessage: string | null;
};

type CreatedEntity = {
  kind: "library" | "indexer" | "client" | "qualityProfile" | "customFormat" | "movie" | "series";
  id: string;
};

type SetupCompletion = {
  message: string;
  libraries: string[];
  qualityProfiles: string[];
  customFormatCount: number;
  indexerName: string | null;
  clientName: string | null;
  firstTitle: string | null;
  firstTitlePath: string | null;
};

function toSetupDraft(form: GuideForm) {
  return {
    mode: form.mode, mediaIntent: form.mediaIntent, movieRootPath: form.movieRootPath, seriesRootPath: form.seriesRootPath,
    qualityPreset: form.qualityPreset, formatGoal: form.formatGoal,
    indexerName: form.indexerName, indexerProtocol: form.indexerProtocol, indexerUrl: form.indexerUrl,
    clientName: form.clientName, clientProtocol: form.clientProtocol, clientHost: form.clientHost, clientPort: form.clientPort,
    backupEnabled: form.backupEnabled,
    firstTitleType: form.firstTitleType, firstTitle: form.firstTitle, firstTitleYear: form.firstTitleYear,
    firstTitleMonitored: form.firstTitleMonitored
  };
}

const STEPS: { id: StepId; label: string; copy: string }[] = [
  { id: "mode", label: "Your library", copy: "Choose what you want Deluno to manage." },
  { id: "folders", label: "Folders", copy: "Tell Deluno where your media lives." },
  { id: "quality", label: "Library Profiles", copy: "Choose the quality and release experience you want." },
  { id: "services", label: "Find & Download", copy: "Connect search sources and downloads when you are ready." },
  { id: "finish", label: "Start", copy: "Create your plan and open the Dashboard." }
];

const QUALITY_PRESETS = {
  balanced1080p: {
    label: "Balanced 1080p",
    cutoff: "WEB 1080p",
    allowed: "WEB 720p,Bluray 720p,WEB 1080p,Bluray 1080p",
    copy: "Best default for most homes: good quality, reasonable size, broad compatibility."
  },
  premium4k: {
    label: "Premium 4K",
    cutoff: "WEB 2160p",
    allowed: "WEB 1080p,Bluray 1080p,WEB 2160p,Bluray 2160p,Remux 2160p",
    copy: "For larger displays and strong storage. Deluno still accepts 1080p while searching for better versions."
  }
} as const;

const FORMAT_GOALS = {
  simpleClean: {
    label: "Just make it work",
    bundleId: "starter-streaming-1080p",
    copy: "Reliable 1080p releases, obvious junk blocked, and no fragile HDR/audio rules.",
    bestFor: "Most first-time installs, family libraries, laptops, and users who want fewer surprises."
  },
  balanced: {
    label: "Balanced library",
    bundleId: "balanced-1080p",
    copy: "Prefers good WEB groups, useful audio, proper/repack fixes, and common bad-release blocking.",
    bestFor: "Everyday movie and TV libraries where quality matters but storage should stay sensible."
  },
  homeTheater: {
    label: "Home theater",
    bundleId: "premium-4k-streaming",
    copy: "Prioritises 4K WEB, HDR, Dolby Vision, stronger release groups, and better living-room playback.",
    bestFor: "4K TVs, Apple TV, Shield, Plex/Jellyfin/Emby users, and premium libraries."
  },
  storageSaver: {
    label: "Storage friendly",
    bundleId: "storage-saver",
    copy: "Discourages huge files, remux-sized releases, generated HDR, and other storage-heavy matches.",
    bestFor: "NAS limits, remote users, slower upload links, and very large libraries."
  },
} as const;

const INDEXER_SETUP_PRESETS = [
  {
    id: "torznab",
    label: "Torznab",
    protocol: "torznab" as const,
    url: "https://your-tracker.com/api",
    copy: "For torrent trackers and private sites that expose a Torznab API."
  },
  {
    id: "newznab",
    label: "Newznab",
    protocol: "newznab" as const,
    url: "https://indexer.example/api",
    copy: "Use for NZBGeek, DrunkenSlug, NZBCat, and similar Usenet indexers."
  },
  {
    id: "rss",
    label: "RSS feed",
    protocol: "rss" as const,
    url: "https://example.com/feed.xml",
    copy: "Simple feed mode for custom/public sources."
  }
] as const;

const CLIENT_SETUP_PRESETS = [
  { protocol: "qbittorrent" as const, label: "qBittorrent", port: "8080", copy: "Best default torrent client for most users." },
  { protocol: "sabnzbd" as const, label: "SABnzbd", port: "8080", copy: "Best default Usenet downloader." },
  { protocol: "transmission" as const, label: "Transmission", port: "9091", copy: "Lightweight torrent client for Linux/NAS installs." },
  { protocol: "deluge" as const, label: "Deluge", port: "8112", copy: "Flexible torrent client with Web UI support." },
  { protocol: "nzbget" as const, label: "NZBGet", port: "6789", copy: "Efficient Usenet downloader for lower-resource systems." },
  { protocol: "utorrent" as const, label: "uTorrent", port: "8080", copy: "Legacy Web UI support for migrations." }
] as const;

export async function setupGuideLoader(): Promise<SetupGuideLoaderData> {
  const [settings, libraries, qualityProfiles, customFormats, indexers, clients, metadataStatus, progress, draft] = await Promise.all([
    fetchJson<PlatformSettingsSnapshot>("/api/settings"),
    fetchJson<LibraryItem[]>("/api/libraries"),
    fetchJson<QualityProfileItem[]>("/api/quality-profiles"),
    fetchJson<CustomFormatItem[]>("/api/custom-formats"),
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<DownloadClientItem[]>("/api/download-clients"),
    fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null),
    fetchJson<SetupProgressItem>("/api/setup/progress"),
    fetchJson<SetupDraftItem>("/api/setup/draft")
  ]);

  return { clients, customFormats, draft, indexers, libraries, metadataStatus, progress, qualityProfiles, settings };
}

export function SetupGuidePage() {
  const loaderData = useLoaderData() as SetupGuideLoaderData;
  const data = loaderData;
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const returnTo = params.get("return") || "/";
  const [stepIndex, setStepIndex] = useState(() => Math.min(STEPS.length - 1, Math.max(0, data.progress.lastCompletedStep)));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<SetupCompletion | null>(null);
  const [metadataResults, setMetadataResults] = useState<MetadataSearchResult[]>([]);
  const [metadataBusy, setMetadataBusy] = useState(false);
  const [rollbackMessage, setRollbackMessage] = useState<string | null>(null);
  const [serviceTest, setServiceTest] = useState<ServiceTestState>({
    indexer: "idle",
    client: "idle",
    indexerMessage: null,
    clientMessage: null
  });
  const [form, setForm] = useState<GuideForm>(() => ({
    mode: data.draft.mode as GuideForm["mode"],
    mediaIntent: data.draft.mediaIntent as GuideForm["mediaIntent"],
    movieRootPath: data.draft.movieRootPath || data.settings.movieRootPath || "",
    seriesRootPath: data.draft.seriesRootPath || data.settings.seriesRootPath || "",
    qualityPreset: data.draft.qualityPreset as GuideForm["qualityPreset"],
    formatGoal: data.draft.formatGoal === "anime" ? "balanced" : (data.draft.formatGoal as GuideForm["formatGoal"]),
    indexerName: data.draft.indexerName || data.indexers[0]?.name || "Primary indexer",
    indexerProtocol: (data.draft.indexerProtocol || data.indexers[0]?.protocol || "torznab") as GuideForm["indexerProtocol"],
    indexerUrl: data.draft.indexerUrl || data.indexers[0]?.baseUrl || "",
    indexerApiKey: data.indexers[0]?.apiKey ?? "",
    clientName: data.draft.clientName || data.clients[0]?.name || "Primary download client",
    clientProtocol: (data.draft.clientProtocol || data.clients[0]?.protocol || "qbittorrent") as GuideForm["clientProtocol"],
    clientHost: data.draft.clientHost || data.clients[0]?.host || "",
    clientPort: data.draft.clientPort || (data.clients[0]?.port ? String(data.clients[0].port) : "8080"),
    clientUsername: data.clients[0]?.username ?? "",
    clientPassword: "",
    backupEnabled: data.draft.backupEnabled,
    firstTitleType: data.draft.firstTitleType as GuideForm["firstTitleType"],
    firstTitle: data.draft.firstTitle,
    firstTitleYear: data.draft.firstTitleYear,
    firstTitleMonitored: data.draft.firstTitleMonitored,
    firstTitleMetadata: null
  }));

  const current = STEPS[stepIndex];
  const hasMovies = form.mediaIntent === "movies" || form.mediaIntent === "both";
  const hasTv = form.mediaIntent === "tv" || form.mediaIntent === "both";
  const canCreateMovies = hasMovies && form.movieRootPath.trim().length > 0;
  const canCreateTv = hasTv && form.seriesRootPath.trim().length > 0;
  const hasQualityChoice = Boolean(form.qualityPreset);
  const hasReleaseRuleChoice = Boolean(form.formatGoal);
  const hasReadyIndexer = data.indexers.some((item) => item.isEnabled && item.healthStatus === "healthy") || serviceTest.indexer === "passed";
  const hasReadyClient = data.clients.some((item) => item.isEnabled && item.healthStatus === "healthy") || serviceTest.client === "passed";
  const indexerConnectionRequested = Boolean(form.indexerUrl.trim()) && !hasReadyIndexer;
  const clientConnectionRequested = Boolean(form.clientHost.trim()) && !hasReadyClient;
  const canFinish =
    (canCreateMovies || canCreateTv) &&
    hasQualityChoice &&
    hasReleaseRuleChoice &&
    hasReadyIndexer &&
    hasReadyClient &&
    !indexerConnectionRequested &&
    !clientConnectionRequested;

  useEffect(() => {
    const timer = window.setTimeout(() => {
      void authedFetch("/api/setup/draft", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(toSetupDraft(form))
      }).catch(() => undefined);
    }, 500);
    return () => window.clearTimeout(timer);
  }, [form]);

  const completion = useMemo(() => {
    const checks = [
      { label: "Account created", done: true },
      { label: "Media root chosen", done: canCreateMovies || canCreateTv },
      { label: "Library Profile chosen", done: hasQualityChoice },
      { label: "Release scoring chosen", done: hasReleaseRuleChoice },
      { label: "Search source ready", done: hasReadyIndexer },
      { label: "Downloads ready", done: hasReadyClient }
    ];
    return checks;
  }, [
    canCreateMovies,
    canCreateTv,
    hasReadyClient,
    hasReadyIndexer,
    form.formatGoal,
    form.qualityPreset,
    serviceTest.client,
    serviceTest.indexer,
  ]);

  function patch(patchValue: Partial<GuideForm>) {
    if (["indexerName", "indexerProtocol", "indexerUrl", "indexerApiKey"].some((key) => key in patchValue)) {
      setServiceTest((current) => ({ ...current, indexer: "idle", indexerMessage: null }));
    }
    if (["clientName", "clientProtocol", "clientHost", "clientPort", "clientUsername", "clientPassword"].some((key) => key in patchValue)) {
      setServiceTest((current) => ({ ...current, client: "idle", clientMessage: null }));
    }
    setForm((currentForm) => ({ ...currentForm, ...patchValue }));
  }

  function saveProgress(lastCompletedStep: number, isSkipped = false, isCompleted = false) {
    return authedFetch("/api/setup/progress", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ lastCompletedStep, isSkipped, isCompleted })
    });
  }

  function moveToStep(nextStep: number) {
    const boundedStep = Math.min(STEPS.length - 1, Math.max(0, nextStep));
    setStepIndex(boundedStep);
    void saveProgress(boundedStep).catch(() => undefined);
  }

  function skipWizard() {
    // The expert path is an explicit choice, not an incomplete setup. The
    // persisted setup status keeps the dashboard prompt in sync per install.
    void saveProgress(stepIndex, true).catch(() => undefined);
    navigate(returnTo, { replace: true });
  }

  async function handleFinish(event?: FormEvent) {
    event?.preventDefault();
    setError(null);
    setResult(null);
    setRollbackMessage(null);

    if (!canFinish) {
      if (!canCreateMovies && !canCreateTv) {
        setError("Choose at least one Movies or TV root folder before finishing simple setup.");
        setStepIndex(1);
      } else if (!hasQualityChoice || !hasReleaseRuleChoice) {
        setError("Choose a picture quality and release preference before finishing simple setup.");
        setStepIndex(2);
      } else if (!hasReadyIndexer || !hasReadyClient || indexerConnectionRequested || clientConnectionRequested) {
        setError(!hasReadyIndexer && !hasReadyClient
          ? "Test at least one search source and one download client before Deluno can be marked operationally ready."
          : !hasReadyIndexer
            ? "Test at least one search source before Deluno can be marked operationally ready."
            : "Test at least one download client before Deluno can be marked operationally ready.");
        setStepIndex(3);
      }
      return;
    }

    setBusy(true);
    const createdEntities: CreatedEntity[] = [];
    try {
      await saveSettings(data.settings, form);
      const qualityProfileCache = [...data.qualityProfiles];
      const customFormatCache = [...data.customFormats];
      const movieCustomFormatIds = hasMovies ? await ensureCustomFormats(customFormatCache, "movies", form, createdEntities) : [];
      const tvCustomFormatIds = hasTv ? await ensureCustomFormats(customFormatCache, "tv", form, createdEntities) : [];
      const movieProfile = hasMovies ? await ensureQualityProfile(qualityProfileCache, "movies", form, movieCustomFormatIds, createdEntities) : null;
      const tvProfile = hasTv ? await ensureQualityProfile(qualityProfileCache, "tv", form, tvCustomFormatIds, createdEntities) : null;
      const indexer = await ensureIndexer(data.indexers, form, serviceTest.indexer === "passed", createdEntities);
      const client = await ensureClient(data.clients, form, serviceTest.client === "passed", createdEntities);
      const movieLibrary = canCreateMovies
        ? await ensureLibrary(data.libraries, "movies", form.movieRootPath, movieProfile?.id ?? null, form, createdEntities)
        : null;
      const tvLibrary = canCreateTv
        ? await ensureLibrary(data.libraries, "tv", form.seriesRootPath, tvProfile?.id ?? null, form, createdEntities)
        : null;

      await Promise.all([
        movieLibrary && indexer && client ? saveRouting(movieLibrary.id, indexer.id, client.id) : Promise.resolve(),
        tvLibrary && indexer && client ? saveRouting(tvLibrary.id, indexer.id, client.id) : Promise.resolve()
      ]);

      const firstTitle = form.firstTitle.trim()
        ? await createFirstTitle(form, createdEntities)
        : null;

      const created = [movieLibrary ? "Movies" : null, tvLibrary ? "TV Shows" : null].filter(Boolean).join(" and ");
      const completionResult = {
        message: `${created || "Library"} baseline created${firstTitle ? ` and "${firstTitle.title}" was added` : ""}.`,
        libraries: [movieLibrary?.name, tvLibrary?.name].filter((item): item is string => Boolean(item)),
        qualityProfiles: [movieProfile?.name, tvProfile?.name].filter((item, index, arr): item is string => Boolean(item) && arr.indexOf(item) === index),
        customFormatCount: createdEntities.filter((item) => item.kind === "customFormat").length,
        indexerName: indexer?.name ?? null,
        clientName: client?.name ?? null,
        firstTitle: firstTitle?.title ?? null,
        firstTitlePath: firstTitle ? (form.firstTitleType === "movies" ? "/movies" : "/tv") : null
      };

      await recordSetupCompleted(completionResult).catch(() => undefined);
      await authedFetch("/api/setup/draft", { method: "DELETE" }).catch(() => undefined);
      setResult(completionResult);
    } catch (err) {
      if (createdEntities.length > 0) {
        const rollback = await rollbackCreatedEntities(createdEntities);
        setRollbackMessage(rollback);
      }
      setError(err instanceof Error ? err.message : "Setup could not be completed.");
    } finally {
      setBusy(false);
    }
  }

  async function handleMetadataSearch() {
    const title = form.firstTitle.trim();
    if (!title) {
      setError("Type a movie or TV title before searching metadata.");
      return;
    }
    if (data.metadataStatus && !data.metadataStatus.isConfigured) {
      setError("Title matching is temporarily unavailable. You can still add this title manually and try metadata search again later.");
      return;
    }

    setError(null);
    setMetadataBusy(true);
    setMetadataResults([]);
    try {
      const params = new URLSearchParams({
        mediaType: form.firstTitleType,
        query: title
      });
      if (form.firstTitleYear.trim()) params.set("year", form.firstTitleYear.trim());
      const results = await fetchJson<MetadataSearchResult[]>(`/api/metadata/search?${params.toString()}`);
      setMetadataResults(results);
      if (results.length === 0) setError("No metadata matches were found. You can still add the title manually.");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Metadata lookup failed.");
    } finally {
      setMetadataBusy(false);
    }
  }

  function selectMetadataResult(item: MetadataSearchResult) {
    patch({
      firstTitle: item.title,
      firstTitleYear: item.year ? String(item.year) : form.firstTitleYear,
      firstTitleMetadata: item
    });
    void enrichSelectedMetadata(item);
  }

  async function enrichSelectedMetadata(item: MetadataSearchResult) {
    try {
      const params = new URLSearchParams({
        mediaType: form.firstTitleType,
        query: item.title,
        providerId: item.providerId
      });
      if (item.year) params.set("year", String(item.year));
      const details = await fetchJson<MetadataSearchResult[]>(`/api/metadata/search?${params.toString()}`);
      const detailedItem = details.find((candidate) => candidate.providerId === item.providerId);
      if (!detailedItem) return;

      setForm((currentForm) =>
        currentForm.firstTitleMetadata?.provider === item.provider && currentForm.firstTitleMetadata.providerId === item.providerId
          ? {
              ...currentForm,
              firstTitle: detailedItem.title,
              firstTitleYear: detailedItem.year ? String(detailedItem.year) : currentForm.firstTitleYear,
              firstTitleMetadata: detailedItem
            }
          : currentForm);
    } catch {
      // The chosen card remains valid; enrichment will be available from the title page later.
    }
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      <section className="relative overflow-hidden rounded-3xl border border-hairline bg-card p-[var(--tile-pad)] shadow-sm">
        <div aria-hidden className="absolute inset-x-0 top-0 h-px bg-gradient-to-r from-transparent via-primary/70 to-transparent" />
        <div className="grid gap-[var(--grid-gap)] 2xl:grid-cols-[minmax(0,1fr)_380px] 2xl:items-stretch">
          <div>
            <p className="flex items-center gap-2 text-[length:var(--type-caption)] font-bold uppercase tracking-[0.18em] text-primary">
              <Sparkles className="h-4 w-4" />
              Guided setup
            </p>
            {/* The topbar already names this page; a second h1 made two. */}
            <p className="mt-2 max-w-5xl font-display text-[clamp(2rem,2.4vw,3.15rem)] font-semibold leading-[0.98] tracking-tight text-foreground">
              Get Deluno working first. Tune it later.
            </p>
            <p className="mt-3 max-w-4xl text-[length:var(--type-body)] leading-relaxed text-muted-foreground">
              This sets up the complete acquisition path: media folders, quality settings, a tested search source, and a tested download client.
              Advanced users can skip the guide, but the setup overview will keep the installation incomplete until the acquisition path is ready.
            </p>
            <SummaryStrip
              className="mt-4"
              cells={[
                { label: "Folders", value: "Movies, TV, downloads" },
                { label: "Quality", value: "Simple profile choice" },
                { label: "Release scoring", value: "Plain-English rules" },
                { label: "Routing", value: "Optional source + client" }
              ]}
            />
          </div>
          <ListCard title="Readiness" count={`${completion.filter((item) => item.done).length} of ${completion.length} done`}>
            <ListTable columns={[{ label: "Step" }, { label: "State", width: LIST_TRACK.status, mobile: true }]} chevron={false}>
              {completion.map((item) => (
                <ListRow key={item.label}>
                  <ListNameCell name={item.label} />
                  <ListCell mobile>
                    <Chip tone={item.done ? "ok" : "muted"}>{item.done ? "Done" : "Waiting"}</Chip>
                  </ListCell>
                </ListRow>
              ))}
            </ListTable>
          </ListCard>
        </div>
      </section>

      <div className="grid gap-[var(--grid-gap)] xl:grid-cols-[260px_minmax(0,1fr)]">
        <aside className="space-y-2">
          {STEPS.map((step, index) => {
            const active = index === stepIndex;
            const done = index < stepIndex;
            return (
              <button
                key={step.id}
                type="button"
                onClick={() => moveToStep(index)}
                className={cn(
                  "w-full rounded-2xl border p-4 text-left transition",
                  active ? "border-primary/40 bg-primary/10" : "border-hairline bg-card hover:border-primary/25",
                  done && "opacity-85"
                )}
              >
                <span className="flex items-center gap-3">
                  <span className="flex h-8 w-8 items-center justify-center rounded-xl border border-hairline bg-background text-xs font-bold tabular-nums text-foreground">
                    {done ? <CheckCircle2 className="h-4 w-4 text-success" /> : index + 1}
                  </span>
                  <span>
                    <span className="block font-semibold text-foreground">{step.label}</span>
                    <span className="mt-0.5 block text-xs leading-relaxed text-muted-foreground">{step.copy}</span>
                  </span>
                </span>
              </button>
            );
          })}
        </aside>

        <form onSubmit={(event) => void handleFinish(event)} className="rounded-3xl border border-hairline bg-card shadow-sm">
          <div className="border-b border-hairline p-[var(--tile-pad)]">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div>
                <h2 className="font-display text-2xl font-semibold tracking-tight text-foreground">{current.label}</h2>
                <p className="mt-1 text-sm text-muted-foreground">{current.copy}</p>
              </div>
              <Badge variant={form.mode === "simple" ? "success" : "default"}>{form.mode === "simple" ? "Simple path" : "Advanced skip"}</Badge>
            </div>
          </div>

          <div className="min-h-[180px] p-[var(--tile-pad)]">
            {current.id === "mode" ? <ModeStep form={form} patch={patch} onSkip={skipWizard} /> : null}
            {current.id === "folders" ? <FoldersStep form={form} patch={patch} /> : null}
            {current.id === "quality" ? <QualityStep form={form} patch={patch} /> : null}
            {current.id === "services" ? (
              <ServicesStep
                form={form}
                patch={patch}
                testState={serviceTest}
                onTestIndexer={() => void testIndexerConnection()}
                onTestClient={() => void testClientConnection()}
              />
            ) : null}
            {current.id === "finish" ? (
          <FinishStep
            form={form}
            patch={patch}
            canFinish={canFinish}
            connectionTestsRequired={!hasReadyIndexer || !hasReadyClient || indexerConnectionRequested || clientConnectionRequested}
            result={result}
                error={error}
                rollbackMessage={rollbackMessage}
                metadataStatus={data.metadataStatus}
                metadataResults={metadataResults}
                metadataBusy={metadataBusy}
                existingMetadataConfigured={Boolean(data.metadataStatus?.isConfigured)}
                onMetadataSearch={() => void handleMetadataSearch()}
                onSelectMetadata={selectMetadataResult}
              />
            ) : null}
          </div>

          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-hairline p-[var(--tile-pad)]">
            <Button type="button" variant="outline" onClick={() => (stepIndex === 0 ? skipWizard() : moveToStep(stepIndex - 1))} disabled={busy}>
              {stepIndex === 0 ? <Settings2 className="h-4 w-4" /> : <ArrowLeft className="h-4 w-4" />}
              {stepIndex === 0 ? "Skip to advanced" : "Back"}
            </Button>
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="ghost" asChild>
                <Link to="/settings">Open Media Management</Link>
              </Button>
              {result ? (
                <Button type="button" asChild>
                  <Link to={result.firstTitlePath ?? returnTo}>
                    Continue to Deluno
                    <ArrowRight className="h-4 w-4" />
                  </Link>
                </Button>
              ) : stepIndex < STEPS.length - 1 ? (
                <Button type="button" onClick={() => moveToStep(stepIndex + 1)}>
                  Continue
                  <ArrowRight className="h-4 w-4" />
                </Button>
              ) : (
                <Button type="submit" disabled={busy || !canFinish}>
                  {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Rocket className="h-4 w-4" />}
                  Create baseline
                </Button>
              )}
            </div>
          </div>
        </form>
      </div>
    </div>
  );

  async function testIndexerConnection() {
    if (!form.indexerUrl.trim()) {
      setServiceTest((current) => ({ ...current, indexer: "failed", indexerMessage: "Enter an indexer URL before testing." }));
      return;
    }

    setServiceTest((current) => ({ ...current, indexer: "testing", indexerMessage: null }));
    try {
      const response = await authedFetch("/api/indexers/test", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildIndexerPayload(form))
      });
      if (!response.ok) throw new Error(await response.text().catch(() => "Indexer test failed."));
      const payload = await response.json().catch(() => null) as ConnectionTestResponse | null;
      if (payload?.healthStatus !== "healthy") throw new Error(payload?.message ?? "Indexer test needs attention.");
      setServiceTest((current) => ({ ...current, indexer: "passed", indexerMessage: payload?.message ?? "Indexer test passed." }));
    } catch (err) {
      setServiceTest((current) => ({
        ...current,
        indexer: "failed",
        indexerMessage: err instanceof Error ? err.message : "Indexer test failed."
      }));
    }
  }

  async function testClientConnection() {
    if (!form.clientHost.trim()) {
      setServiceTest((current) => ({ ...current, client: "failed", clientMessage: "Enter a download client host before testing." }));
      return;
    }

    setServiceTest((current) => ({ ...current, client: "testing", clientMessage: null }));
    try {
      const response = await authedFetch("/api/download-clients/test", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildClientPayload(form))
      });
      if (!response.ok) throw new Error(await response.text().catch(() => "Download client test failed."));
      const payload = await response.json().catch(() => null) as ConnectionTestResponse | null;
      if (payload?.healthStatus !== "healthy") throw new Error(payload?.message ?? "Download client test needs attention.");
      setServiceTest((current) => ({ ...current, client: "passed", clientMessage: payload?.message ?? "Download client test passed." }));
    } catch (err) {
      setServiceTest((current) => ({
        ...current,
        client: "failed",
        clientMessage: err instanceof Error ? err.message : "Download client test failed."
      }));
    }
  }
}

function ModeStep({ form, patch, onSkip }: { form: GuideForm; patch: (patchValue: Partial<GuideForm>) => void; onSkip: () => void }) {
  return (
    <div className="grid gap-[var(--grid-gap)] lg:grid-cols-2">
      <ChoiceCard
        active={form.mode === "simple"}
        icon={Rocket}
        title="Simple configuration"
        copy="Recommended. Deluno creates a clean baseline you can trust, then you can refine advanced policies later."
        onClick={() => patch({ mode: "simple" })}
      />
      <ChoiceCard
        active={form.mode === "advanced"}
        icon={Settings2}
        title="I know what I am doing"
        copy="Skip the wizard and go straight to the full settings, routing, quality, indexer, and policy controls."
        onClick={() => {
          patch({ mode: "advanced" });
          onSkip();
        }}
      />
      <div className="lg:col-span-2 rounded-2xl border border-hairline bg-surface-1 p-4">
        <p className="text-sm font-semibold text-foreground">What Deluno will do</p>
        <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
          It will not hide advanced controls. It simply sets safe defaults first: folders, naming, quality target,
          retry-friendly automation, optional provider routing, and backups.
        </p>
      </div>
    </div>
  );
}

function FoldersStep({ form, patch }: { form: GuideForm; patch: (patchValue: Partial<GuideForm>) => void }) {
  return (
    <div className="space-y-[var(--page-gap)]">
      <div className="grid gap-3 sm:grid-cols-3">
        {(["both", "movies", "tv"] as const).map((value) => (
          <button
            key={value}
            type="button"
            onClick={() => patch({ mediaIntent: value })}
            className={cn(
              "rounded-2xl border p-4 text-left transition",
              form.mediaIntent === value ? "border-primary/40 bg-primary/10" : "border-hairline bg-surface-1"
            )}
          >
            <p className="font-semibold capitalize text-foreground">{value === "tv" ? "TV Shows" : value}</p>
            <p className="mt-1 text-xs text-muted-foreground">Create {value === "both" ? "Movies and TV" : value === "tv" ? "TV" : "Movies"} library defaults.</p>
          </button>
        ))}
      </div>

      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <p className="text-sm font-semibold text-foreground">Beginner default</p>
        <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
          Movies use <span className="font-semibold text-foreground">{`{Movie Title} ({Release Year})`}</span> and TV shows use{" "}
          <span className="font-semibold text-foreground">{`{Series Title} ({Series Year})`}</span>. Advanced users can change naming later.
        </p>
      </div>

      <div className="grid gap-[var(--grid-gap)] lg:grid-cols-2">
        {(form.mediaIntent === "movies" || form.mediaIntent === "both") ? (
          <FieldShell icon={FolderTree} label="Movies root" copy="Where movie folders should live. UNC, Docker mount, mapped drives, and local paths are supported.">
            <PathInput value={form.movieRootPath} onChange={(value) => patch({ movieRootPath: value })} placeholder="D:\\Media\\Movies or /media/movies" browseTitle="Choose movies root" />
          </FieldShell>
        ) : null}
        {(form.mediaIntent === "tv" || form.mediaIntent === "both") ? (
          <FieldShell icon={FolderTree} label="TV root" copy="Where series folders should live.">
            <PathInput value={form.seriesRootPath} onChange={(value) => patch({ seriesRootPath: value })} placeholder="D:\\Media\\TV or /media/tv" browseTitle="Choose TV root" />
          </FieldShell>
        ) : null}
      </div>
    </div>
  );
}

function QualityStep({ form, patch }: { form: GuideForm; patch: (patchValue: Partial<GuideForm>) => void }) {
  const selectedGoal = form.formatGoal ? FORMAT_GOALS[form.formatGoal] : null;
  const selectedBundle = form.formatGoal ? getFormatBundle(form.formatGoal) : null;

  return (
    <div className="space-y-[var(--page-gap)]">
      <section>
        <div className="mb-3">
          <p className="text-sm font-semibold text-foreground">Picture quality</p>
          <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
            Choose a preset and Deluno handles the details. You can fine-tune everything later.
          </p>
        </div>
        <div className="grid gap-[var(--grid-gap)] lg:grid-cols-2">
          {Object.entries(QUALITY_PRESETS).map(([id, preset]) => (
            <ChoiceCard
              key={id}
              active={form.qualityPreset === id}
              icon={ShieldCheck}
              title={preset.label}
              copy={`${preset.copy} Target: ${preset.cutoff}.`}
              onClick={() => patch({ qualityPreset: id as Exclude<GuideForm["qualityPreset"], ""> })}
            />
          ))}
        </div>
      </section>

      <section>
        <div className="mb-3">
          <p className="text-sm font-semibold text-foreground">Release preference</p>
          <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
            This is custom formats in plain English. Deluno adds the matching rules automatically; advanced users can edit them later.
          </p>
        </div>
        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
          {Object.entries(FORMAT_GOALS).map(([id, goal]) => (
            <button
              key={id}
              type="button"
              onClick={() => patch({ formatGoal: id as Exclude<GuideForm["formatGoal"], ""> })}
              className={cn(
                "rounded-2xl border p-4 text-left transition hover:-translate-y-0.5",
                form.formatGoal === id ? "border-primary/40 bg-primary/10" : "border-hairline bg-surface-1 hover:border-primary/25"
              )}
            >
              <Sparkles className={cn("h-4 w-4", form.formatGoal === id ? "text-primary" : "text-muted-foreground")} />
              <p className="mt-3 font-semibold text-foreground">{goal.label}</p>
              <p className="mt-1 text-sm leading-relaxed text-muted-foreground">{goal.copy}</p>
              <p className="mt-3 text-xs leading-relaxed text-muted-foreground">{goal.bestFor}</p>
            </button>
          ))}
        </div>
      </section>

      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="text-sm font-semibold text-foreground">What will be created</p>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
              {selectedGoal ? `${selectedGoal.label}: ${selectedBundle?.description ?? selectedGoal.copy}` : "Choose a release preference to preview the rules Deluno will create."}
            </p>
          </div>
          <Badge variant="default">{selectedBundle?.includes.length ?? 0} rules</Badge>
        </div>
        <div className="mt-4 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
          {(form.formatGoal ? previewFormats(form.formatGoal) : []).map((format) => (
            <div key={format.trashId} className="rounded-xl border border-hairline bg-background/35 px-3 py-2">
              <p className="text-sm font-semibold text-foreground">{format.name}</p>
              <p className="mt-1 line-clamp-2 text-xs leading-relaxed text-muted-foreground">{format.description}</p>
            </div>
          ))}
        </div>
        <p className="mt-4 text-sm leading-relaxed text-muted-foreground">
          Automation defaults: missing searches, upgrade searches, 12 hour scheduled checks, 6 hour retry delay, and a small per-run cap so indexers are not hammered.
        </p>
      </div>
    </div>
  );
}

function ServicesStep({
  form,
  patch,
  testState,
  onTestIndexer,
  onTestClient
}: {
  form: GuideForm;
  patch: (patchValue: Partial<GuideForm>) => void;
  testState: ServiceTestState;
  onTestIndexer: () => void;
  onTestClient: () => void;
}) {
  return (
    <div className="grid gap-[var(--grid-gap)] xl:grid-cols-2">
      <FieldShell icon={RadioTower} label="Search source" copy="Required for operational readiness. Connect and test the source Deluno will use to find releases.">
        <div className="grid gap-3">
          <PresetRadioGroup
            label="Search source presets"
            options={INDEXER_SETUP_PRESETS.map((preset) => ({
              id: preset.id,
              label: preset.label,
              copy: preset.copy,
              active: form.indexerProtocol === preset.protocol && form.indexerName === preset.label,
              onSelect: () => patch({
                indexerName: preset.label,
                indexerProtocol: preset.protocol,
                indexerUrl: preset.url
              })
            }))}
          />
          <Field label="Indexer name" hideLabel>
            <Input value={form.indexerName} onChange={(event) => patch({ indexerName: event.target.value })} placeholder="Primary indexer" />
          </Field>
          <Field label="Indexer protocol" hideLabel>
            <Select value={form.indexerProtocol} onChange={(event) => patch({ indexerProtocol: event.target.value as GuideForm["indexerProtocol"] })}>
              <option value="torznab">Torznab</option>
              <option value="newznab">Newznab (Usenet indexer)</option>
              <option value="rss">RSS feed</option>
            </Select>
          </Field>
          <Field label="Indexer URL" hideLabel>
            <Input value={form.indexerUrl} onChange={(event) => patch({ indexerUrl: event.target.value })} placeholder="https://indexer.example/api" />
          </Field>
          <Field label="Indexer API key" hideLabel>
            <Input value={form.indexerApiKey} onChange={(event) => patch({ indexerApiKey: event.target.value })} placeholder="API key, if required" />
          </Field>
          <ServiceTestButton
            status={testState.indexer}
            message={testState.indexerMessage}
            onClick={onTestIndexer}
            label="Test search source"
          />
        </div>
      </FieldShell>

      <FieldShell icon={DownloadCloud} label="Download client" copy="Required for operational readiness. The external client owns downloading; Deluno orchestrates, observes, and imports.">
        <div className="grid gap-3">
          <PresetRadioGroup
            label="Download client presets"
            options={CLIENT_SETUP_PRESETS.map((preset) => ({
              id: preset.protocol,
              label: preset.label,
              copy: preset.copy,
              active: form.clientProtocol === preset.protocol,
              onSelect: () => patch({
                clientName: preset.label,
                clientProtocol: preset.protocol,
                clientPort: preset.port
              })
            }))}
          />
          <Field label="Download client name" hideLabel>
            <Input value={form.clientName} onChange={(event) => patch({ clientName: event.target.value })} placeholder="Primary download client" />
          </Field>
          <Field label="Download client protocol" hideLabel>
            <Select value={form.clientProtocol} onChange={(event) => patch({ clientProtocol: event.target.value as GuideForm["clientProtocol"], clientPort: defaultClientPort(event.target.value) })}>
              <option value="qbittorrent">qBittorrent</option>
              <option value="sabnzbd">SABnzbd</option>
              <option value="transmission">Transmission</option>
              <option value="deluge">Deluge</option>
              <option value="nzbget">NZBGet</option>
              <option value="utorrent">uTorrent</option>
            </Select>
          </Field>
          <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_120px]">
            <Field label="Download client host" hideLabel>
              <Input value={form.clientHost} onChange={(event) => patch({ clientHost: event.target.value })} placeholder="localhost or docker host" />
            </Field>
            <Field label="Download client port" hideLabel>
              <Input value={form.clientPort} onChange={(event) => patch({ clientPort: event.target.value })} placeholder="8080" />
            </Field>
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Download client username" hideLabel>
              <Input value={form.clientUsername} onChange={(event) => patch({ clientUsername: event.target.value })} placeholder="Username, if required" />
            </Field>
            <Field label="Download client password or API key" hideLabel>
              <Input value={form.clientPassword} onChange={(event) => patch({ clientPassword: event.target.value })} placeholder="Password/API key" type="password" />
            </Field>
          </div>
          <ServiceTestButton
            status={testState.client}
            message={testState.clientMessage}
            onClick={onTestClient}
            label="Test download client"
          />
        </div>
      </FieldShell>
    </div>
  );
}

function FinishStep({
  form,
  patch,
  canFinish,
  connectionTestsRequired,
  result,
  error,
  rollbackMessage,
  metadataStatus,
  metadataResults,
  metadataBusy,
  existingMetadataConfigured,
  onMetadataSearch,
  onSelectMetadata
}: {
  form: GuideForm;
  patch: (patchValue: Partial<GuideForm>) => void;
  canFinish: boolean;
  connectionTestsRequired: boolean;
  result: SetupCompletion | null;
  error: string | null;
  rollbackMessage: string | null;
  metadataStatus: MetadataProviderStatus | null;
  metadataResults: MetadataSearchResult[];
  metadataBusy: boolean;
  existingMetadataConfigured: boolean;
  onMetadataSearch: () => void;
  onSelectMetadata: (item: MetadataSearchResult) => void;
}) {
  if (result) {
    return <SetupComplete result={result} />;
  }

  return (
    <div className="space-y-[var(--page-gap)]">
      <SummaryStrip
        cells={[
          { label: "Libraries", value: form.mediaIntent === "both" ? "Movies + TV" : form.mediaIntent === "tv" ? "TV" : "Movies" },
          { label: "Quality", value: form.qualityPreset ? QUALITY_PRESETS[form.qualityPreset].label : "Not chosen", tone: form.qualityPreset ? undefined : "warning" },
          { label: "Release scoring", value: form.formatGoal ? FORMAT_GOALS[form.formatGoal].label : "Not chosen", tone: form.formatGoal ? undefined : "warning" },
          { label: "Indexer", value: form.indexerUrl.trim() ? form.indexerProtocol : "Later" },
          { label: "Client", value: form.clientHost.trim() ? form.clientProtocol : "Later" }
        ]}
      />
      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="text-sm font-semibold text-foreground">Import decision preview</p>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
              This is the plain-English route Deluno will use before importing anything.
            </p>
          </div>
          <Badge variant={form.clientHost.trim() ? "success" : "warning"}>
            {form.clientHost.trim() ? "Client route ready" : "Client can be added later"}
          </Badge>
        </div>
        <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
          {buildImportPreviewRows(form).map((row) => (
            <div key={row.label} className="rounded-xl border border-hairline bg-background/35 p-3">
              <p className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.16em] text-muted-foreground">{row.label}</p>
              <p className="mt-1 text-sm font-semibold text-foreground">{row.value}</p>
              <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{row.copy}</p>
            </div>
          ))}
        </div>
      </div>
      {!canFinish ? (
        <p className="rounded-xl border border-warning/25 bg-warning/10 p-4 text-sm text-warning">
          {connectionTestsRequired
            ? "Test at least one search source and one download client before Deluno can be marked operationally ready."
            : "Choose at least one library root folder, picture quality, and release preference before Deluno can create the baseline."}
        </p>
      ) : null}
      {error ? <p className="rounded-xl border border-destructive/25 bg-destructive/10 p-4 text-sm text-destructive">{error}</p> : null}
      {rollbackMessage ? <p className="rounded-xl border border-warning/25 bg-warning/10 p-4 text-sm text-warning">{rollbackMessage}</p> : null}
      <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="flex items-center gap-2 text-sm font-semibold text-foreground">
              <Plus className="h-4 w-4 text-primary" />
              Add your first title
            </p>
            <p className="mt-1 text-sm leading-relaxed text-muted-foreground">
              Optional. Add one movie or show now so Deluno can immediately start monitoring it after setup.
            </p>
          </div>
          <div className="flex rounded-xl border border-hairline bg-background/35 p-1">
            {(["movies", "tv"] as const).map((value) => (
              <button
                key={value}
                type="button"
                onClick={() => patch({ firstTitleType: value })}
                className={cn(
                  "rounded-lg px-3 py-1.5 text-sm font-semibold transition",
                  form.firstTitleType === value ? "bg-primary text-primary-foreground" : "text-muted-foreground hover:text-foreground"
                )}
              >
                {value === "movies" ? "Movie" : "TV"}
              </button>
            ))}
          </div>
        </div>
        <div className="mt-4 grid gap-3 lg:grid-cols-[minmax(0,1fr)_120px_160px]">
          <Input
            value={form.firstTitle}
            onChange={(event) => patch({ firstTitle: event.target.value })}
            placeholder={form.firstTitleType === "movies" ? "Dune: Part Two" : "Severance"}
          />
          <Input
            value={form.firstTitleYear}
            onChange={(event) => patch({ firstTitleYear: event.target.value.replace(/\D/g, "").slice(0, 4) })}
            placeholder="Year"
            inputMode="numeric"
          />
          <label className="flex h-[var(--control-height)] items-center gap-2 rounded-xl border border-hairline bg-surface-2 px-3 text-sm font-semibold text-foreground">
            <Checkbox
              checked={form.firstTitleMonitored}
              onCheckedChange={(firstTitleMonitored) => patch({ firstTitleMonitored })}
            />
            Monitor
          </label>
        </div>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          {!existingMetadataConfigured ? (
            <p className="w-full rounded-xl border border-warning/25 bg-warning/10 p-3 text-xs leading-relaxed text-warning">
              Title matching is not available on this Deluno installation yet. You can finish setup and add this title manually; the administrator can restore matching later without changing your library setup.
            </p>
          ) : null}
          <Button
            type="button"
            variant="secondary"
            onClick={onMetadataSearch}
            disabled={metadataBusy || !existingMetadataConfigured}
            className="gap-2"
          >
            {metadataBusy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Search metadata
          </Button>
          <Badge variant={metadataStatus?.isConfigured ? "success" : "warning"}>
            {metadataStatus?.isConfigured ? "Title matching ready" : "Manual title entry"}
          </Badge>
          {form.firstTitleMetadata ? (
            <Badge variant="info">{form.firstTitleMetadata.provider.toUpperCase()} #{form.firstTitleMetadata.providerId}</Badge>
          ) : null}
        </div>
        {metadataResults.length > 0 ? (
          <div className="mt-3 grid gap-2 md:grid-cols-2 xl:grid-cols-3">
            {metadataResults.slice(0, 6).map((item) => (
              <button
                key={`${item.provider}:${item.providerId}`}
                type="button"
                onClick={() => onSelectMetadata(item)}
                className={cn(
                  "flex min-w-0 gap-3 rounded-xl border p-2 text-left transition hover:border-primary/35",
                  form.firstTitleMetadata?.provider === item.provider && form.firstTitleMetadata.providerId === item.providerId
                    ? "border-primary/45 bg-primary/10"
                    : "border-hairline bg-background/35"
                )}
              >
                {item.posterUrl ? (
                  <img src={item.posterUrl} alt="" className="h-16 w-11 rounded-lg object-cover" />
                ) : (
                  <div className="flex h-16 w-11 items-center justify-center rounded-lg bg-muted text-xs text-muted-foreground">No art</div>
                )}
                <span className="min-w-0">
                  <span className="block truncate text-sm font-semibold text-foreground">{item.title}</span>
                  <span className="mt-0.5 block text-xs text-muted-foreground">{item.year ?? "Unknown year"} · {item.provider.toUpperCase()}</span>
                  {item.rating ? <span className="mt-1 block text-[length:var(--type-caption)] tabular-nums text-primary">{item.rating.toFixed(1)} rating</span> : null}
                </span>
              </button>
            ))}
          </div>
        ) : null}
      </div>
      <div className="grid gap-3 lg:grid-cols-3">
        <HandoffTile
          title="Review routing"
          copy="See which indexer and download client each library uses."
          to="/indexers"
        />
        <HandoffTile
          title="Tune quality"
          copy="Adjust generated profiles and custom formats when you are ready."
          to="/settings/profiles"
        />
        <HandoffTile
          title="Watch automation"
          copy="Track scheduled searches, retry windows, queue health, and audit events."
          to="/system"
        />
      </div>
      <p className="text-sm leading-relaxed text-muted-foreground">
        Nothing here is permanent. After setup, open Media Management to refine final destinations, release scoring, quality profiles,
        metadata preferences, tags, multi-library routing, and automation.
      </p>
    </div>
  );
}

function SetupComplete({ result }: { result: SetupCompletion }) {
  return (
    <div className="space-y-[var(--page-gap)]">
      <div className="rounded-2xl border border-success/25 bg-success/10 p-5">
        <p className="flex items-center gap-2 font-display text-2xl font-semibold tracking-tight text-foreground">
          <CheckCircle2 className="h-5 w-5 text-success" />
          Setup complete
        </p>
        <p className="mt-2 text-sm leading-relaxed text-muted-foreground">{result.message} Review the summary below before moving on.</p>
      </div>
      <SummaryStrip
        cells={[
          { label: "Libraries", value: result.libraries.join(" + ") || "None" },
          { label: "Quality Profiles", value: result.qualityProfiles.join(" + ") || "Existing" },
          { label: "Release rules", value: result.customFormatCount > 0 ? `${result.customFormatCount} created` : "Reused existing" },
          { label: "Indexer", value: result.indexerName ?? "Later", help: result.indexerName ? "connected" : "add one when you are ready" },
          { label: "First title", value: result.firstTitle ?? "Skipped", help: result.clientName ? `via ${result.clientName}` : "no download client yet" }
        ]}
      />
      <div className="grid gap-3 lg:grid-cols-4">
        <HandoffTile title="Dashboard" copy="See automation, provider health, queue, and recent activity." to="/" />
        <HandoffTile title="Add titles" copy="Browse and monitor the library you just created." to={result.firstTitlePath ?? "/movies"} />
        <HandoffTile title="Review routing" copy="Confirm indexer and download-client routing." to="/indexers" />
        <HandoffTile title="Media Management" copy="Tune plans, quality, final destinations, metadata, and automation." to="/settings" />
      </div>
    </div>
  );
}

function HandoffTile({ title, copy, to }: { title: string; copy: string; to: string }) {
  return (
    <Link
      to={to}
      className="rounded-2xl border border-hairline bg-surface-1 p-4 transition hover:-translate-y-0.5 hover:border-primary/35"
    >
      <p className="text-sm font-semibold text-foreground">{title}</p>
      <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{copy}</p>
    </Link>
  );
}

function ServiceTestButton({
  status,
  message,
  label,
  onClick
}: {
  status: ServiceTestState["indexer"];
  message: string | null;
  label: string;
  onClick: () => void;
}) {
  const tone =
    status === "passed"
      ? "text-success"
      : status === "failed"
        ? "text-destructive"
        : "text-muted-foreground";

  return (
    <div className="rounded-xl border border-hairline bg-background/35 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <Button type="button" variant="secondary" onClick={onClick} disabled={status === "testing"} className="gap-2">
          {status === "testing" ? <Loader2 className="h-4 w-4 animate-spin" /> : <Wifi className="h-4 w-4" />}
          {label}
        </Button>
        <span className={cn("text-xs font-semibold", tone)}>
          {status === "idle" ? "Not tested" : status === "testing" ? "Testing..." : status === "passed" ? "Passed" : "Failed"}
        </span>
      </div>
      {message ? <p className={cn("mt-2 text-xs leading-relaxed", tone)}>{message}</p> : null}
    </div>
  );
}

interface PresetRadioOption {
  id: string;
  label: string;
  copy: string;
  active: boolean;
  onSelect: () => void;
}

function PresetRadioGroup({ label, options }: { label: string; options: PresetRadioOption[] }) {
  const refs = useRef<Array<HTMLButtonElement | null>>([]);
  const activeIndex = options.findIndex((option) => option.active);
  const tabIndex = activeIndex >= 0 ? activeIndex : 0;

  function moveFocus(index: number, direction: -1 | 1) {
    if (options.length === 0) return;
    const nextIndex = (index + direction + options.length) % options.length;
    refs.current[nextIndex]?.focus();
    options[nextIndex]?.onSelect();
  }

  return (
    <div role="radiogroup" aria-label={label} className="grid gap-2 sm:grid-cols-2">
      {options.map((option, index) => (
        <button
          key={option.id}
          ref={(element) => { refs.current[index] = element; }}
          type="button"
          role="radio"
          aria-checked={option.active}
          tabIndex={index === tabIndex ? 0 : -1}
          onClick={option.onSelect}
          onKeyDown={(event) => {
            if (event.key === "ArrowRight" || event.key === "ArrowDown") {
              event.preventDefault();
              moveFocus(index, 1);
            } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
              event.preventDefault();
              moveFocus(index, -1);
            }
          }}
          className={cn(
            "rounded-xl border p-3 text-left transition hover:border-primary/35",
            option.active ? "border-primary/45 bg-primary/10" : "border-hairline bg-background/35"
          )}
        >
          <p className="text-sm font-semibold text-foreground">{option.label}</p>
          <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{option.copy}</p>
        </button>
      ))}
    </div>
  );
}

function ChoiceCard({ active, icon: Icon, title, copy, onClick }: { active: boolean; icon: LucideIcon; title: string; copy: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "rounded-2xl border p-5 text-left transition hover:-translate-y-0.5",
        active ? "border-primary/40 bg-primary/10" : "border-hairline bg-surface-1 hover:border-primary/25"
      )}
    >
      <Icon className={cn("h-5 w-5", active ? "text-primary" : "text-muted-foreground")} />
      <p className="mt-4 font-display text-xl font-semibold tracking-tight text-foreground">{title}</p>
      <p className="mt-2 text-sm leading-relaxed text-muted-foreground">{copy}</p>
    </button>
  );
}

function FieldShell({ icon: Icon, label, copy, children }: { icon: LucideIcon; label: string; copy: string; children: ReactNode }) {
  return (
    <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
      <div className="mb-4 flex gap-3">
        <Icon className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
        <div>
          <p className="font-semibold text-foreground">{label}</p>
          <p className="mt-1 text-xs leading-relaxed text-muted-foreground">{copy}</p>
        </div>
      </div>
      {children}
    </div>
  );
}

function buildImportPreviewRows(form: GuideForm) {
  const roots = [
    form.mediaIntent !== "tv" && form.movieRootPath.trim() ? `Movies -> ${form.movieRootPath.trim()}` : null,
    form.mediaIntent !== "movies" && form.seriesRootPath.trim() ? `TV -> ${form.seriesRootPath.trim()}` : null
  ].filter(Boolean).join(" / ");

  return [
    {
      label: "Search",
      value: form.indexerUrl.trim() ? form.indexerName || form.indexerProtocol : "Search source required",
      copy: form.indexerUrl.trim()
        ? `Uses ${form.indexerProtocol.toUpperCase()} categories for ${form.mediaIntent === "both" ? "movies and TV" : form.mediaIntent}.`
        : "Deluno cannot be marked operationally ready until a search source is configured and tested."
    },
    {
      label: "Download",
      value: form.clientHost.trim() ? form.clientName || form.clientProtocol : "Download client required",
      copy: form.clientHost.trim()
        ? `Routes to ${form.clientProtocol} with deluno-movies and deluno-tv categories.`
        : "Approved releases will not be sent anywhere until a download client is configured."
    },
    {
      label: "Destination",
      value: roots || "Choose root folders",
      copy: "Destination rules can later override these roots by genre, tag, language, studio, or title."
    },
    {
      label: "Transfer",
      value: form.backupEnabled ? "Single-copy link first + backups" : "Single-copy link first",
      copy: "Imports prefer a single-copy link (hardlink) when storage allows, then fall back to safe copy or move behaviour."
    }
  ];
}

async function recordSetupCompleted(result: SetupCompletion) {
  const response = await authedFetch("/api/setup/completed", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      libraries: result.libraries,
      qualityProfiles: result.qualityProfiles,
      customFormatCount: result.customFormatCount,
      indexerName: result.indexerName,
      clientName: result.clientName,
      firstTitle: result.firstTitle
    })
  });

  if (!response.ok) {
    throw new Error(await response.text().catch(() => "Setup audit event could not be recorded."));
  }
}

async function saveSettings(settings: PlatformSettingsSnapshot, form: GuideForm) {
  const payload = {
    appInstanceName: settings.appInstanceName || "Deluno",
    movieRootPath: form.movieRootPath.trim() || settings.movieRootPath,
    seriesRootPath: form.seriesRootPath.trim() || settings.seriesRootPath,
    downloadsPath: settings.downloadsPath,
    incompleteDownloadsPath: settings.incompleteDownloadsPath,
    autoStartJobs: true,
    enableNotifications: settings.enableNotifications,
    renameOnImport: true,
    useHardlinks: settings.useHardlinks,
    cleanupEmptyFolders: true,
    removeCompletedDownloads: settings.removeCompletedDownloads,
    unmonitorWhenCutoffMet: settings.unmonitorWhenCutoffMet,
    movieFolderFormat: settings.movieFolderFormat || "{Movie Title} ({Release Year})",
    seriesFolderFormat: settings.seriesFolderFormat || "{Series Title} ({Series Year})",
    episodeFileFormat: settings.episodeFileFormat || "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
    hostBindAddress: settings.hostBindAddress || "127.0.0.1",
    hostPort: settings.hostPort || 5099,
    urlBase: settings.urlBase || "",
    requireAuthentication: true,
    uiTheme: settings.uiTheme || "system",
    uiDensity: settings.uiDensity || "comfortable",
    defaultMovieView: settings.defaultMovieView || "grid",
    defaultShowView: settings.defaultShowView || "grid",
    metadataNfoEnabled: settings.metadataNfoEnabled,
    metadataArtworkEnabled: settings.metadataArtworkEnabled,
    metadataCertificationCountry: settings.metadataCertificationCountry || "US",
    metadataLanguage: settings.metadataLanguage || "en",
    metadataProviderMode: settings.metadataProviderMode || "direct",
    metadataBrokerUrl: settings.metadataBrokerUrl || ""
  };

  return await fetchJson<PlatformSettingsSnapshot>("/api/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });
}

type SelectedFormatGoal = Exclude<GuideForm["formatGoal"], "">;
type SelectedQualityPreset = Exclude<GuideForm["qualityPreset"], "">;

function getFormatBundle(goal: SelectedFormatGoal): CustomFormatBundle | undefined {
  return CUSTOM_FORMAT_BUNDLES.find((bundle) => bundle.id === FORMAT_GOALS[goal].bundleId);
}

function getEffectiveFormatGoal(_mediaType: "movies" | "tv", form: GuideForm): SelectedFormatGoal | null {
  if (!form.formatGoal) return null;
  return form.formatGoal;
}

function getEffectiveFormatBundle(mediaType: "movies" | "tv", form: GuideForm): CustomFormatBundle | undefined {
  const goal = getEffectiveFormatGoal(mediaType, form);
  return goal ? getFormatBundle(goal) : undefined;
}

function previewFormats(goal: SelectedFormatGoal): BundledCF[] {
  const bundle = getFormatBundle(goal);
  if (!bundle) return [];
  return bundle.includes
    .slice(0, 6)
    .map((entry) => findBundledCF(entry.trashId))
    .filter((format): format is BundledCF => Boolean(format));
}

async function ensureCustomFormats(existing: CustomFormatItem[], mediaType: "movies" | "tv", form: GuideForm, createdEntities: CreatedEntity[]): Promise<string[]> {
  const bundle = getEffectiveFormatBundle(mediaType, form);
  if (!bundle) return [];

  const ids: string[] = [];
  for (const entry of bundle.includes) {
    const format = findBundledCF(entry.trashId);
    if (!format) continue;

    const existingFormat = existing.find(
      (item) => item.trashId === format.trashId && (item.mediaType === mediaType || item.mediaType === "all")
    );
    if (existingFormat) {
      ids.push(existingFormat.id);
      continue;
    }

    const created = await fetchJson<CustomFormatItem>("/api/custom-formats", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: format.name,
        mediaType,
        score: entry.score ?? format.defaultScore,
        trashId: format.trashId,
        conditions: format.patterns.map((pattern) => `regex: ${pattern}`).join("\n"),
        upgradeAllowed: true
      })
    });
    existing.push(created);
    createdEntities.push({ kind: "customFormat", id: created.id });
    ids.push(created.id);
  }

  return ids;
}

async function ensureQualityProfile(
  existing: QualityProfileItem[],
  mediaType: "movies" | "tv",
  form: GuideForm,
  customFormatIds: string[],
  createdEntities: CreatedEntity[]
) {
  const reusableProfile = existing.find((item) => item.mediaType === mediaType);
  if (!form.qualityPreset) {
    if (reusableProfile) return reusableProfile;
    throw new Error("Choose a picture quality before Deluno can create a quality profile.");
  }
  const preset = QUALITY_PRESETS[form.qualityPreset as SelectedQualityPreset];
  const effectiveGoal = getEffectiveFormatGoal(mediaType, form);
  if (!effectiveGoal) {
    if (reusableProfile) return reusableProfile;
    throw new Error("Choose a release preference before Deluno can create release rules.");
  }
  const goal = FORMAT_GOALS[effectiveGoal];
  const profileName = `${preset.label} - ${goal.label}`;
  const existingProfile = existing.find((item) => item.mediaType === mediaType && item.name === profileName);
  if (existingProfile) return existingProfile;

  const created = await fetchJson<QualityProfileItem>("/api/quality-profiles", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: profileName,
      mediaType,
      cutoffQuality: preset.cutoff,
      allowedQualities: preset.allowed,
      customFormatIds: customFormatIds.join(","),
      upgradeUntilCutoff: true,
      upgradeUnknownItems: form.qualityPreset === "premium4k"
    })
  });
  existing.push(created);
  createdEntities.push({ kind: "qualityProfile", id: created.id });
  return created;
}

async function ensureIndexer(existing: IndexerItem[], form: GuideForm, tested: boolean, createdEntities: CreatedEntity[]) {
  if (!form.indexerUrl.trim()) return null;
  if (!tested) throw new Error("Test the search source before saving it.");

  const item = existing[0]
    ? await fetchJson<IndexerItem>(`/api/indexers/${existing[0].id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildIndexerPayload(form))
      })
    : await fetchJson<IndexerItem>("/api/indexers", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildIndexerPayload(form))
      });
  if (!existing[0]) createdEntities.push({ kind: "indexer", id: item.id });

  const persistedTest = await fetchJson<ConnectionTestResponse>(`/api/indexers/${item.id}/test`, { method: "POST" });
  if (persistedTest.healthStatus !== "healthy") {
    throw new Error(persistedTest.message || "Search source did not pass its persisted connection test.");
  }
  return item;
}

async function ensureClient(existing: DownloadClientItem[], form: GuideForm, tested: boolean, createdEntities: CreatedEntity[]) {
  if (!form.clientHost.trim()) return null;
  if (!tested) throw new Error("Test the download destination before saving it.");

  const item = existing[0]
    ? await fetchJson<DownloadClientItem>(`/api/download-clients/${existing[0].id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildClientPayload(form))
      })
    : await fetchJson<DownloadClientItem>("/api/download-clients", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(buildClientPayload(form))
      });
  if (!existing[0]) createdEntities.push({ kind: "client", id: item.id });

  const persistedTest = await fetchJson<ConnectionTestResponse>(`/api/download-clients/${item.id}/test`, { method: "POST" });
  if (persistedTest.healthStatus !== "healthy") {
    throw new Error(persistedTest.message || "Download destination did not pass its persisted connection test.");
  }
  return item;
}

function buildIndexerPayload(form: GuideForm) {
  return {
    name: form.indexerName.trim() || "Primary indexer",
    protocol: form.indexerProtocol,
    privacy: form.indexerProtocol === "newznab" ? "usenet" : "private",
    baseUrl: form.indexerUrl.trim(),
    apiKey: form.indexerApiKey.trim() || null,
    priority: 10,
    categories: form.mediaIntent === "tv"
      ? "5000,5010,5020,5030,5040,5045,5050"
      : form.mediaIntent === "movies"
        ? "2000,2010,2020,2030,2040,2045,2050"
        : "2000,2010,2020,2030,2040,2045,2050,5000,5010,5020,5030,5040,5045,5050",
    tags: "",
    mediaScope: form.mediaIntent === "both" ? "both" : form.mediaIntent,
    isEnabled: true
  };
}

function buildClientPayload(form: GuideForm) {
  return {
    name: form.clientName.trim() || "Primary download client",
    protocol: form.clientProtocol,
    host: form.clientHost.trim(),
    port: Number(form.clientPort || defaultClientPort(form.clientProtocol)),
    username: form.clientUsername.trim() || null,
    password: form.clientPassword || null,
    endpointUrl: null,
    moviesCategory: "deluno-movies",
    tvCategory: "deluno-tv",
    categoryTemplate: "deluno-{mediaType}",
    priority: 10,
    isEnabled: true
  };
}

async function ensureLibrary(
  existing: LibraryItem[],
  mediaType: "movies" | "tv",
  rootPath: string,
  qualityProfileId: string | null,
  form: GuideForm,
  createdEntities: CreatedEntity[]
) {
  const existingLibrary = existing.find((item) => item.mediaType === mediaType);
  if (existingLibrary) return existingLibrary;

  const created = await fetchJson<LibraryItem>("/api/libraries", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      name: mediaType === "movies" ? "Movies" : "TV Shows",
      mediaType,
      purpose: "Main library",
      rootPath,
      downloadsPath: null,
      qualityProfileId,
      autoSearchEnabled: true,
      missingSearchEnabled: true,
      upgradeSearchEnabled: true,
      searchIntervalHours: 12,
      retryDelayHours: 6,
      maxItemsPerRun: form.qualityPreset === "premium4k" ? 15 : 10
    })
  });
  createdEntities.push({ kind: "library", id: created.id });
  return created;
}

async function createFirstTitle(form: GuideForm, createdEntities: CreatedEntity[]): Promise<{ id: string; title: string }> {
  const title = form.firstTitle.trim();
  if (!title) throw new Error("First title is empty.");

  const year = form.firstTitleYear.trim() ? Number(form.firstTitleYear.trim()) : null;
  const endpoint = form.firstTitleType === "movies" ? "/api/movies" : "/api/series";
  const payload =
    form.firstTitleType === "movies"
      ? {
          title,
          releaseYear: year,
          imdbId: form.firstTitleMetadata?.imdbId ?? null,
          monitored: form.firstTitleMonitored,
          ...metadataCreatePayload(form.firstTitleMetadata)
        }
      : {
          title,
          startYear: year,
          imdbId: form.firstTitleMetadata?.imdbId ?? null,
          monitored: form.firstTitleMonitored,
          ...metadataCreatePayload(form.firstTitleMetadata)
        };

  const response = await authedFetch(endpoint, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    throw new Error(await response.text().catch(() => `Could not add ${title}.`));
  }

  const created = await response.json() as { id: string; title: string };
  createdEntities.push({ kind: form.firstTitleType === "movies" ? "movie" : "series", id: created.id });
  return created;
}

function metadataCreatePayload(metadata: MetadataSearchResult | null) {
  if (!metadata) return {};
  return {
    metadataProvider: metadata.provider,
    metadataProviderId: metadata.providerId,
    originalTitle: metadata.originalTitle,
    overview: metadata.overview,
    posterUrl: metadata.posterUrl,
    backdropUrl: metadata.backdropUrl,
    rating: metadata.rating,
    genres: metadata.genres.join(", "),
    externalUrl: metadata.externalUrl,
    metadataJson: JSON.stringify(metadata)
  };
}

async function rollbackCreatedEntities(createdEntities: CreatedEntity[]) {
  const reversed = [...createdEntities].reverse();
  let removed = 0;
  for (const entity of reversed) {
    const endpoint = rollbackEndpoint(entity);
    if (!endpoint) continue;
    try {
      const response = await authedFetch(endpoint, { method: "DELETE" });
      if (response.ok || response.status === 404 || response.status === 204) removed += 1;
    } catch {
      // Best effort rollback. The error shown to the user remains the original setup failure.
    }
  }
  return removed > 0
    ? `Setup failed, so Deluno cleaned up ${removed} partially created item${removed === 1 ? "" : "s"}.`
    : "Setup failed before Deluno created anything that needed cleanup.";
}

function rollbackEndpoint(entity: CreatedEntity) {
  switch (entity.kind) {
    case "library":
      return `/api/libraries/${entity.id}`;
    case "indexer":
      return `/api/indexers/${entity.id}`;
    case "client":
      return `/api/download-clients/${entity.id}`;
    case "qualityProfile":
      return `/api/quality-profiles/${entity.id}`;
    case "customFormat":
      return `/api/custom-formats/${entity.id}`;
    default:
      return null;
  }
}

async function saveRouting(libraryId: string, indexerId: string, downloadClientId: string) {
  const response = await authedFetch(`/api/libraries/${libraryId}/routing`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      sources: [{ indexerId, priority: 1, requiredTags: "", excludedTags: "" }],
      downloadClients: [{ downloadClientId, priority: 1 }]
    })
  });
  if (!response.ok) {
    throw new Error(await response.text().catch(() => "Routing could not be saved."));
  }
}

function defaultClientPort(protocol: string) {
  return {
    qbittorrent: "8080",
    sabnzbd: "8080",
    transmission: "9091",
    deluge: "8112",
    nzbget: "6789",
    utorrent: "8080"
  }[protocol] ?? "8080";
}

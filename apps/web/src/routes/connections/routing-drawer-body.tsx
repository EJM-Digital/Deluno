import type { DownloadClientItem, IndexerItem, LibraryItem } from "../../lib/api";
import { Chip } from "../../components/ui/chip";
import { Button } from "../../components/ui/button";
import { DrawerSection } from "../../components/ui/drawer";
import { TagPicker } from "../../components/app/tag-picker";
import { Input } from "../../components/ui/input";
import { Switch } from "../../components/ui/switch";
import type { DownloadClientCategoryCheckResult } from "../../lib/api";
import { healthChip, protocolLabel } from "./format";
import { FolderChainSection } from "./folder-chain-section";
export function RoutingDrawerBody({
  library,
  indexers,
  clients,
  sources,
  targets,
  categories,
  sourceTags,
  onSourceTagsChange,
  categoryChecks,
  onToggleSource,
  onToggleClient,
  onCategoryChange,
  onCheckCategory,
  busy,
  folderCheckKey
}: {
  library: LibraryItem;
  indexers: IndexerItem[];
  clients: DownloadClientItem[];
  sources: string[];
  targets: string[];
  categories: Record<string, string>;
  sourceTags: Record<string, { required: string; excluded: string }>;
  onSourceTagsChange: (id: string, patch: { required?: string; excluded?: string }) => void;
  categoryChecks: Record<string, DownloadClientCategoryCheckResult>;
  onToggleSource: (id: string, on: boolean) => void;
  onToggleClient: (id: string, on: boolean) => void;
  onCategoryChange: (id: string, value: string) => void;
  onCheckCategory: (id: string) => void;
  busy: string | null;
  /** Changes when the routing is saved, so the folder check reads it again. */
  folderCheckKey?: unknown;
}) {
  const isTv = library.mediaType === "tv";
  const relevantIndexers = indexers.filter((item) => (item.mediaScope ?? "both") === "both" || (item.mediaScope ?? "both") === (isTv ? "tv" : "movies"));

  return (
    <>
      <div className="mb-4 rounded-[10px] border border-hairline bg-surface-2/40 px-3 py-2.5">
        <p className="text-(length:--type-caption) leading-relaxed text-muted-foreground">
          Choose where this library searches and which download client receives approved releases. When more than one client can take a download, the one with the lowest priority number goes first; clients with the same number take turns, and a client whose recent downloads mostly failed is passed over.
        </p>
      </div>
      <DrawerSection title="Indexers" aside={`${sources.length} of ${relevantIndexers.length} · only ${isTv ? "TV" : "movie"}-capable indexers are listed`}>
        {relevantIndexers.length ? (
          <div className="grid gap-2">
            {relevantIndexers.map((item) => {
              const chip = healthChip(item);
              return (
                <div key={item.id} className="rounded-[10px] border border-hairline px-(--field-pad-x)"><div className="flex min-h-10 items-center justify-between gap-(--grid-gap)">
                  <label htmlFor={`route-src-${item.id}`} className="min-w-0 cursor-pointer">
                    <span className="block truncate text-(length:--type-body-sm) font-medium text-foreground">{item.name}</span>
                    <span className="block truncate text-(length:--type-caption) text-muted-foreground">{protocolLabel(item.protocol)} · priority {item.priority}</span>
                  </label>
                  <span className="flex items-center gap-3">
                    <Chip tone={chip.tone}>{chip.label}</Chip>
                    <Switch id={`route-src-${item.id}`} size="sm" checked={sources.includes(item.id)} onCheckedChange={(on) => onToggleSource(item.id, on)} />
                  </span>
                </div>
                {sources.includes(item.id) ? (
                  <div className="grid gap-3 border-t border-hairline py-3 sm:grid-cols-2">
                    <div className="grid gap-1.5">
                      <span className="text-(length:--type-caption) font-medium text-foreground">Only for titles tagged (optional)</span>
                      <TagPicker label={`Required tags for ${item.name}`} value={sourceTags[item.id]?.required ?? ""} onChange={(required) => onSourceTagsChange(item.id, { required })} />
                    </div>
                    <div className="grid gap-1.5">
                      <span className="text-(length:--type-caption) font-medium text-foreground">Never for titles tagged (optional)</span>
                      <TagPicker label={`Excluded tags for ${item.name}`} value={sourceTags[item.id]?.excluded ?? ""} onChange={(excluded) => onSourceTagsChange(item.id, { excluded })} />
                    </div>
                  </div>
                ) : null}
                </div>
              );
            })}
          </div>
        ) : (
          <p className="text-(length:--type-caption) text-muted-foreground">No {isTv ? "TV" : "movie"}-capable indexers yet. Add one under Indexers.</p>
        )}
        {sources.length === 0 && relevantIndexers.length ? <p className="text-(length:--type-caption) text-warning">No indexers selected — Deluno can't look for releases for this library.</p> : null}
      </DrawerSection>
      <DrawerSection title="Download clients" aside={`${targets.length} of ${clients.length}`}>
        <p className="text-(length:--type-caption) leading-relaxed text-muted-foreground">
          A category is the label Deluno sends with a download, not a folder path. First create the matching category or rule in the download client, for example a <span className="font-medium text-foreground">family-movies</span> category pointed at the matching downloads folder, then enter <span className="font-medium text-foreground">family-movies</span> in that client's box below. Deluno sends the label with the download and uses it again when the file finishes. A category is optional: use one only when the client has different folders or rules for different libraries. Leave a box empty to use the client’s normal {isTv ? "TV" : "movie"} category. Transmission has no categories: Deluno sends each download to its own folder, so leave its box empty.
        </p>
        {clients.length ? (
          <div className="grid gap-2">
            {clients.map((item) => {
              const chip = healthChip(item);
              const defaultCategory = isTv ? item.tvCategory ?? item.categoryTemplate ?? "" : item.moviesCategory ?? item.categoryTemplate ?? "";
              const routeCategory = categories[item.id]?.trim() ?? "";
              const category = routeCategory || defaultCategory;
              return (
                <div key={item.id} className="rounded-[10px] border border-hairline p-3">
                  <div className="flex min-h-10 items-center justify-between gap-(--grid-gap)">
                    <label htmlFor={`route-cli-${item.id}`} className="min-w-0 cursor-pointer">
                      <span className="block truncate text-(length:--type-body-sm) font-medium text-foreground">{item.name}</span>
                      <span className="block truncate text-(length:--type-caption) text-muted-foreground">
                        {protocolLabel(item.protocol)}{category ? ` · ${routeCategory ? "library label" : "client default"} ${category}` : ""}
                      </span>
                    </label>
                    <span className="flex items-center gap-3">
                      <Chip tone={chip.tone}>{chip.label}</Chip>
                      <Switch id={`route-cli-${item.id}`} size="sm" checked={targets.includes(item.id)} onCheckedChange={(on) => onToggleClient(item.id, on)} />
                    </span>
                  </div>
                  {targets.includes(item.id) ? (
                    <div className="mt-3 grid gap-1.5 border-t border-hairline pt-3">
                      <label htmlFor={`route-category-${item.id}`} className="text-(length:--type-caption) font-medium text-foreground">Optional category sent to the download client</label>
                      <Input
                        id={`route-category-${item.id}`}
                        value={categories[item.id] ?? ""}
                        placeholder={defaultCategory || "For example: family-movies"}
                        onChange={(event) => onCategoryChange(item.id, event.target.value)}
                      />
                      <div className="flex flex-wrap items-center gap-2">
                        <Chip tone={routeCategory ? "info" : "idle"}>
                          {routeCategory ? `Library category: ${routeCategory}` : "Using client default"}
                        </Chip>
                        <span className="text-(length:--type-caption) text-muted-foreground">
                          {routeCategory ? "This label must also exist in the download app." : `Deluno will use the client’s normal ${isTv ? "TV" : "movie"} category.`}
                        </span>
                      </div>
                      <div className="flex flex-wrap items-center gap-2 pt-1">
                        <Button
                          type="button"
                          variant="secondary"
                          size="sm"
                          disabled={!routeCategory || busy === `category:${item.id}`}
                          onClick={() => onCheckCategory(item.id)}
                        >
                          {busy === `category:${item.id}` ? "Checking…" : "Check category"}
                        </Button>
                        {categoryChecks[item.id] ? (
                          <Chip tone={categoryChecks[item.id].status === "ready" ? "ok" : categoryChecks[item.id].status === "missing" ? "warn" : categoryChecks[item.id].status === "unreachable" ? "bad" : "idle"}>
                            {categoryChecks[item.id].status === "ready" ? "Ready" : categoryChecks[item.id].status === "missing" ? "Not found" : categoryChecks[item.id].status === "unreachable" ? "Could not check" : "Manual check needed"}
                          </Chip>
                        ) : null}
                      </div>
                      {categoryChecks[item.id] ? <p role="status" className="text-(length:--type-caption) text-muted-foreground">{categoryChecks[item.id].message}</p> : null}
                    </div>
                  ) : null}
                </div>
              );
            })}
          </div>
        ) : (
          <p className="text-(length:--type-caption) text-muted-foreground">No download clients yet. Add one under Download clients.</p>
        )}
        {targets.length === 0 && sources.length > 0 && clients.length ? <p className="text-(length:--type-caption) text-warning">No download client selected — approved releases have nowhere to go.</p> : null}
      </DrawerSection>
      <FolderChainSection libraryId={library.id} refreshKey={folderCheckKey} />
    </>
  );
}

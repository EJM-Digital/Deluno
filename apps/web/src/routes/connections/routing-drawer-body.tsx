import type { DownloadClientItem, IndexerItem, LibraryItem } from "../../lib/api";
import { Chip } from "../../components/ui/chip";
import { DrawerSection } from "../../components/ui/drawer";
import { Switch } from "../../components/ui/switch";
import { healthChip, protocolLabel } from "./format";
export function RoutingDrawerBody({
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

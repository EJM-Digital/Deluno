import { useLoaderData, useNavigation, useRevalidator } from "react-router-dom";
import { LibraryViewWithBulkOps } from "../components/app/library-view-with-bulk-ops";
import {
  fetchJson,
  type MetadataProviderStatus,
  type MovieListItem,
  type MovieWantedSummary,
  type SeriesListItem,
  type SeriesWantedSummary
} from "../lib/api";
import type { MediaItem } from "../lib/media-types";
import { adaptMovieItems, adaptSeriesItems } from "../lib/ui-adapters";
import { LibraryGridSkeleton } from "../components/shell/skeleton";

async function loadMovieItems(): Promise<MediaItem[]> {
  const [items, wanted] = await Promise.all([
    fetchJson<MovieListItem[]>("/api/movies"),
    fetchJson<MovieWantedSummary>("/api/movies/wanted")
  ]);

  return adaptMovieItems(items, wanted);
}

async function loadShowItems(): Promise<MediaItem[]> {
  const [items, wanted] = await Promise.all([
    fetchJson<SeriesListItem[]>("/api/series"),
    fetchJson<SeriesWantedSummary>("/api/series/wanted")
  ]);

  return adaptSeriesItems(items, wanted);
}

export async function moviesLoader() {
  const [items, metadataStatus] = await Promise.all([
    loadMovieItems(),
    fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null)
  ]);

  return { items, metadataStatus };
}

export async function showsLoader() {
  const [items, metadataStatus] = await Promise.all([
    loadShowItems(),
    fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null)
  ]);

  return { items, metadataStatus };
}

export function MoviesPage() {
  const loaderData = useLoaderData() as { items: MediaItem[]; metadataStatus: MetadataProviderStatus | null } | undefined;
  const navigation = useNavigation();
  const revalidator = useRevalidator();
  if (!loaderData) return <LibraryLoadingShell title="Movies" />;
  return (
    <LibraryViewWithBulkOps
      variant="movies"
      items={loaderData.items}
      metadataStatus={loaderData.metadataStatus}
      isRouteLoading={navigation.state !== "idle"}
      onReload={() => revalidator.revalidate()}
    />
  );
}

export function ShowsPage() {
  const loaderData = useLoaderData() as { items: MediaItem[]; metadataStatus: MetadataProviderStatus | null } | undefined;
  const navigation = useNavigation();
  const revalidator = useRevalidator();
  if (!loaderData) return <LibraryLoadingShell title="TV Shows" />;
  return (
    <LibraryViewWithBulkOps
      variant="shows"
      items={loaderData.items}
      metadataStatus={loaderData.metadataStatus}
      isRouteLoading={navigation.state !== "idle"}
      onReload={() => revalidator.revalidate()}
    />
  );
}

function LibraryLoadingShell({ title }: { title: string }) {
  return (
    <div className="space-y-[var(--page-gap)]">
      <div className="space-y-2">
        <p className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.22em] text-muted-foreground">
          Browse, filter, and route media
        </p>
        <h1 className="font-display text-[length:var(--type-title-lg)] font-semibold tracking-display text-foreground">{title}</h1>
      </div>
      <div className="rounded-2xl border border-hairline bg-card p-[var(--tile-pad)]">
        <LibraryGridSkeleton count={20} />
      </div>
    </div>
  );
}

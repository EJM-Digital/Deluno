import { useLoaderData, useRevalidator } from "react-router-dom";
import { LibraryView } from "../components/app/library-view";
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

async function loadMovieItems(): Promise<MediaItem[]> {
  try {
    const [items, wanted] = await Promise.all([
      fetchJson<MovieListItem[]>("/api/movies"),
      fetchJson<MovieWantedSummary>("/api/movies/wanted")
    ]);

    return adaptMovieItems(items, wanted);
  } catch {
    return [];
  }
}

async function loadShowItems(): Promise<MediaItem[]> {
  try {
    const [items, wanted] = await Promise.all([
      fetchJson<SeriesListItem[]>("/api/series"),
      fetchJson<SeriesWantedSummary>("/api/series/wanted")
    ]);

    return adaptSeriesItems(items, wanted);
  } catch {
    return [];
  }
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
  const items = loaderData?.items ?? [];
  const revalidator = useRevalidator();
  return <LibraryView variant="movies" items={items} metadataStatus={loaderData?.metadataStatus ?? null} onReload={() => revalidator.revalidate()} />;
}

export function ShowsPage() {
  const loaderData = useLoaderData() as { items: MediaItem[]; metadataStatus: MetadataProviderStatus | null } | undefined;
  const items = loaderData?.items ?? [];
  const revalidator = useRevalidator();
  return <LibraryView variant="shows" items={items} metadataStatus={loaderData?.metadataStatus ?? null} onReload={() => revalidator.revalidate()} />;
}

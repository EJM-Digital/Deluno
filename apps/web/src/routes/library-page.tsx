import { useLoaderData, useNavigation, useRevalidator } from "react-router-dom";
import { LibraryView } from "../components/app/library-view";
import { fetchJson, type MetadataProviderStatus } from "../lib/api";

export async function moviesLoader() {
  return { metadataStatus: await fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null) };
}

export async function showsLoader() {
  return { metadataStatus: await fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null) };
}

export function MoviesPage() {
  const { metadataStatus } = useLoaderData() as { metadataStatus: MetadataProviderStatus | null };
  const navigation = useNavigation();
  const revalidator = useRevalidator();
  return (
    <LibraryView
      key="movies"
      variant="movies"
      metadataStatus={metadataStatus}
      isRouteLoading={navigation.state !== "idle"}
      onReload={() => revalidator.revalidate()}
    />
  );
}

export function ShowsPage() {
  const { metadataStatus } = useLoaderData() as { metadataStatus: MetadataProviderStatus | null };
  const navigation = useNavigation();
  const revalidator = useRevalidator();
  return (
    <LibraryView
      key="shows"
      variant="shows"
      metadataStatus={metadataStatus}
      isRouteLoading={navigation.state !== "idle"}
      onReload={() => revalidator.revalidate()}
    />
  );
}

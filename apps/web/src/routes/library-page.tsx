import { useLoaderData, useNavigation, useRevalidator } from "react-router-dom";
import { LibraryView } from "../components/app/library-view";
import type { ViewMode } from "../components/app/library-control-rail";
import { fetchJson, type MetadataProviderStatus, type PlatformSettingsSnapshot } from "../lib/api";

/** An unrecognised or missing stored default falls back rather than being trusted (#736). */
function asViewMode(value: string | undefined): ViewMode {
  return value === "list" ? "list" : "grid";
}

export async function moviesLoader() {
  const [metadataStatus, settings] = await Promise.all([
    fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null),
    fetchJson<PlatformSettingsSnapshot>("/api/settings").catch(() => null)
  ]);
  return { metadataStatus, defaultView: asViewMode(settings?.defaultMovieView) };
}

export async function showsLoader() {
  const [metadataStatus, settings] = await Promise.all([
    fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null),
    fetchJson<PlatformSettingsSnapshot>("/api/settings").catch(() => null)
  ]);
  return { metadataStatus, defaultView: asViewMode(settings?.defaultShowView) };
}

export function MoviesPage() {
  const { metadataStatus, defaultView } = useLoaderData() as { metadataStatus: MetadataProviderStatus | null; defaultView: ViewMode };
  const navigation = useNavigation();
  const revalidator = useRevalidator();
  return (
    <LibraryView
      key="movies"
      variant="movies"
      metadataStatus={metadataStatus}
      defaultView={defaultView}
      isRouteLoading={navigation.state !== "idle"}
      onReload={() => revalidator.revalidate()}
    />
  );
}

export function ShowsPage() {
  const { metadataStatus, defaultView } = useLoaderData() as { metadataStatus: MetadataProviderStatus | null; defaultView: ViewMode };
  const navigation = useNavigation();
  const revalidator = useRevalidator();
  return (
    <LibraryView
      key="shows"
      variant="shows"
      metadataStatus={metadataStatus}
      defaultView={defaultView}
      isRouteLoading={navigation.state !== "idle"}
      onReload={() => revalidator.revalidate()}
    />
  );
}

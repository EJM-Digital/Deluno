import { useNavigation } from "react-router-dom";
import { LibraryView } from "../components/app/library-view";
import type { ViewMode } from "../components/app/library-control-rail";
import { RouteSourceGate } from "../components/shell/route-source-gate";
import { fetchJson, type MetadataProviderStatus, type PlatformSettingsSnapshot } from "../lib/api";
import { defineSources, sourceLoader, useRouteSources, type RouteSourceData, type RouteSourceState } from "../lib/route-cache";

/** An unrecognised or missing stored default falls back rather than being trusted (#736). */
function asViewMode(value: string | undefined): ViewMode {
  return value === "list" ? "list" : "grid";
}

/**
 * Movies and TV Shows open at once (#1081): the library itself is read by the
 * view, and what the screen needs first is only which layout you chose. That is
 * a small answer the cache keeps, so a revisit opens straight in your layout.
 */
const librarySources = defineSources({
  metadataStatus: {
    key: ["src", "metadata-status"],
    label: "Metadata status",
    fetch: () => fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null),
    fallback: null as MetadataProviderStatus | null
  },
  settings: {
    key: ["src", "settings-soft"],
    label: "Settings",
    fetch: () => fetchJson<PlatformSettingsSnapshot>("/api/settings").catch(() => null),
    fallback: null as PlatformSettingsSnapshot | null,
    // The layout you chose decides how the first frame is drawn, so it is waited
    // for (it is a few hundred bytes, and cached after the first visit).
    requiredIn: ["library"]
  }
});

export const moviesLoader = sourceLoader(librarySources);
export const showsLoader = sourceLoader(librarySources);

function LibraryRoute({ variant }: { variant: "movies" | "shows" }) {
  const state = useRouteSources(librarySources, "library");
  return (
    <RouteSourceGate state={state}>
      {() => <LibraryRouteBody variant={variant} data={state.data} revalidator={state.revalidator} />}
    </RouteSourceGate>
  );
}

function LibraryRouteBody({ variant, data, revalidator }: { variant: "movies" | "shows"; data: RouteSourceData<typeof librarySources>; revalidator: RouteSourceState<typeof librarySources>["revalidator"] }) {
  const navigation = useNavigation();
  const defaultView = asViewMode(variant === "movies" ? data.settings?.defaultMovieView : data.settings?.defaultShowView);
  return (
    <LibraryView
      key={variant}
      variant={variant}
      metadataStatus={data.metadataStatus}
      defaultView={defaultView}
      isRouteLoading={navigation.state !== "idle"}
      onReload={() => revalidator.revalidate()}
    />
  );
}

export function MoviesPage() {
  return <LibraryRoute variant="movies" />;
}

export function ShowsPage() {
  return <LibraryRoute variant="shows" />;
}

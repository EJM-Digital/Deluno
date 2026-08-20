import { useLoaderData, useNavigation, useRevalidator } from "react-router-dom";
import { LibraryView } from "../components/app/library-view";
import { fetchJson, type MetadataProviderStatus } from "../lib/api";
import { LibraryGridSkeleton } from "../components/shell/skeleton";

export async function moviesLoader() {
  return { metadataStatus: await fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null) };
}

export async function showsLoader() {
  return { metadataStatus: await fetchJson<MetadataProviderStatus>("/api/metadata/status").catch(() => null) };
}

export function MoviesPage() {
  const loaderData = useLoaderData() as { metadataStatus: MetadataProviderStatus | null } | undefined;
  const navigation = useNavigation();
  const revalidator = useRevalidator();
  if (!loaderData) return <LibraryLoadingShell title="Movies" />;
  return (
    <LibraryView
      variant="movies"
      metadataStatus={loaderData.metadataStatus}
      isRouteLoading={navigation.state !== "idle"}
      onReload={() => revalidator.revalidate()}
    />
  );
}

export function ShowsPage() {
  const loaderData = useLoaderData() as { metadataStatus: MetadataProviderStatus | null } | undefined;
  const navigation = useNavigation();
  const revalidator = useRevalidator();
  if (!loaderData) return <LibraryLoadingShell title="TV Shows" />;
  return (
    <LibraryView
      variant="shows"
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
        {/* The topbar is the page heading. A second h1 here made every library
            view carry two, which is what a screen reader reads out. */}
        <p className="font-display text-[length:var(--type-title-lg)] font-semibold tracking-display text-foreground">{title}</p>
      </div>
      <div className="rounded-2xl border border-hairline bg-card p-[var(--tile-pad)]">
        <LibraryGridSkeleton count={20} />
      </div>
    </div>
  );
}

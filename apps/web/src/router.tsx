import type { LoaderFunction } from "react-router-dom";
import { createBrowserRouter, Navigate, redirect, useLoaderData, useParams } from "react-router-dom";
import type { ComponentType } from "react";
import { RouteErrorBoundary } from "./components/shell/route-error-boundary";
import { ConfigurationWorkspaceLayout, SettingsWorkspaceLayout, SystemWorkspaceLayout } from "./components/app/settings-shell";
import { MoviesWorkspaceLayout, TvWorkspaceLayout } from "./components/app/media-workspace-shell";
import { AppLayout } from "./layouts/app-layout";
import { LoginPage } from "./routes/login-page";
import { SetupPage } from "./routes/setup-page";
import { RoutePending, RouteSkeleton } from "./components/shell/skeleton";
import { readStored } from "./lib/use-auth";

function LegacyShowDetailRedirect() {
  const { id } = useParams();
  return <Navigate to={id ? `/tv/${id}` : "/tv"} replace />;
}

type LazyRouteModule = {
  loader?: LoaderFunction;
  Component: ComponentType;
};

/**
 * Tiny helper that wraps `React.lazy`-style loader modules so every
 * route gets a consistent skeleton fallback while it resolves.
 */
function withSkeleton(loadModule: () => Promise<LazyRouteModule>) {
  return async () => {
    const mod = await loadModule();
    const Component = mod.loader
      ? function RouteDataGuard() {
          const loaderData = useLoaderData();
          return loaderData == null ? <RoutePending /> : <mod.Component />;
        }
      : mod.Component;

    return {
      loader: mod.loader
        ? async (args: Parameters<NonNullable<LazyRouteModule["loader"]>>[0]) =>
            (await mod.loader!(args)) ?? null
        : async () => null,
      Component,
      ErrorBoundary: RouteErrorBoundary,
      HydrateFallback: RoutePending
    };
  };
}

async function requiresSetup() {
  try {
    const response = await fetch("/api/auth/bootstrap-status");
    if (!response.ok) {
      return false;
    }

    const payload = (await response.json()) as { requiresSetup?: boolean };
    return payload.requiresSetup === true;
  } catch {
    return false;
  }
}

async function requireSessionLoader({ request }: { request: Request }) {
  if (await requiresSetup()) {
    const url = new URL(request.url);
    const returnTo = `${url.pathname}${url.search}${url.hash}`;
    throw redirect(`/setup?return=${encodeURIComponent(returnTo)}`);
  }

  const { token, user } = readStored();
  if (token && user) {
    return null;
  }

  const url = new URL(request.url);
  const returnTo = `${url.pathname}${url.search}${url.hash}`;
  throw redirect(`/login?return=${encodeURIComponent(returnTo)}`);
}

async function loginLoader() {
  if (await requiresSetup()) {
    throw redirect("/setup");
  }

  const { token, user } = readStored();
  if (token && user) {
    throw redirect("/");
  }

  return null;
}

async function setupLoader({ request }: { request: Request }) {
  if (!(await requiresSetup())) {
    const url = new URL(request.url);
    const returnTo = url.searchParams.get("return");
    throw redirect((returnTo?.startsWith("/") ? returnTo : null) || "/login");
  }

  const { token, user } = readStored();
  if (token && user) {
    return null;
  }

  return null;
}

export const router = createBrowserRouter([
  /* Standalone pages (no shell) */
  {
    path: "/login",
    loader: loginLoader,
    element: <LoginPage />,
    errorElement: <RouteErrorBoundary />,
    HydrateFallback: RouteSkeleton
  },
  {
    path: "/setup",
    loader: setupLoader,
    element: <SetupPage />,
    errorElement: <RouteErrorBoundary />,
    HydrateFallback: RouteSkeleton
  },

  {
    path: "/",
    loader: requireSessionLoader,
    element: <AppLayout />,
    errorElement: <RouteErrorBoundary />,
    HydrateFallback: RouteSkeleton,
    children: [
      {
        index: true,
        lazy: withSkeleton(async () => {
          const module = await import("./routes/dashboard-page");
          return { loader: module.dashboardLoader, Component: module.DashboardPage };
        })
      },
      {
        path: "setup-guide",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/setup-guide-page");
          return { loader: module.setupGuideLoader, Component: module.SetupGuidePage };
        })
      },
      {
        path: "movies",
        element: <MoviesWorkspaceLayout />,
        children: [
          {
            index: true,
            lazy: withSkeleton(async () => {
              const module = await import("./routes/library-page");
              return { loader: module.moviesLoader, Component: module.MoviesPage };
            })
          },
          {
            path: "wanted",
            element: <Navigate to="/movies?filter=missing" replace />
          },
          {
            path: "upgrades",
            element: <Navigate to="/movies?filter=upgrades" replace />
          },
          {
            path: "import",
            element: <Navigate to="/movies?filter=needsAttention" replace />
          },
          { path: "library", element: <Navigate to="/movies" replace /> }
        ]
      },
      {
        path: "movies/:id",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/movie-detail-page");
          return { loader: module.movieDetailLoader, Component: module.MovieDetailPage };
        })
      },
      {
        path: "tv",
        element: <TvWorkspaceLayout />,
        children: [
          {
            index: true,
            lazy: withSkeleton(async () => {
              const module = await import("./routes/library-page");
              return { loader: module.showsLoader, Component: module.ShowsPage };
            })
          },
          {
            path: "wanted",
            element: <Navigate to="/tv?filter=missing" replace />
          },
          {
            path: "episodes",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/episode-search-page");
              return { loader: module.episodeSearchLoader, Component: module.EpisodeSearchPage };
            })
          },
          {
            path: "upgrades",
            element: <Navigate to="/tv?filter=upgrades" replace />
          },
          {
            path: "import",
            element: <Navigate to="/tv?filter=needsAttention" replace />
          },
          { path: "library", element: <Navigate to="/tv" replace /> }
        ]
      },
      {
        path: "tv/:id",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/show-detail-page");
          return { loader: module.showDetailLoader, Component: module.ShowDetailPage };
        })
      },
      { path: "shows", element: <Navigate to="/tv" replace /> },
      { path: "shows/library", element: <Navigate to="/tv" replace /> },
      { path: "shows/:id", element: <LegacyShowDetailRedirect /> },
      {
        path: "collections",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/collections-page");
          return { loader: module.collectionsLoader, Component: module.CollectionsPage };
        })
      },
      {
        path: "calendar",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/calendar-page");
          return { loader: module.calendarLoader, Component: module.CalendarPage };
        })
      },
      {
        path: "activity",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/activity-page");
          return { loader: module.activityLoader, Component: module.ActivityPage };
        })
      },
      {
        path: "queue",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/queue-page");
          return { loader: module.queueLoader, Component: module.QueuePage };
        })
      },
      {
        element: <ConfigurationWorkspaceLayout />,
        children: [
      {
        path: "indexers",
        element: <Navigate to="/indexers/indexers" replace />
      },
      {
        path: "indexers/indexers",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/connections-screen");
          return { loader: module.indexersLoader, Component: module.IndexersPage };
        })
      },
      {
        path: "indexers/scoreboard",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/connections-screen");
          return { loader: module.indexersLoader, Component: module.IndexersPage };
        })
      },
      {
        path: "indexers/download-clients",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/connections-screen");
          return { loader: module.indexersLoader, Component: module.IndexersPage };
        })
      },
      {
        path: "indexers/library-routing",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/connections-screen");
          return { loader: module.indexersLoader, Component: module.IndexersPage };
        })
      },
      {
        // An aspect of a library, beside the other aspects of a library — and
        // every library on one screen, because "English on everything, Japanese
        // on anime" is a comparison you make across them.
        path: "settings/subtitles",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/subtitle-languages-screen");
          return { loader: module.subtitleLanguagesLoader, Component: module.SubtitleLanguagesPage };
        })
      },
      {
        // A source, beside the other sources. It needs no download client,
        // because the file arrives in the answer.
        path: "indexers/subtitle-providers",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/subtitle-providers-screen");
          return { loader: module.subtitleProvidersLoader, Component: module.SubtitleProvidersPage };
        })
      },
      {
        path: "search-cycles",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/search-cycles-page");
          return { loader: module.searchCyclesLoader, Component: module.SearchCyclesPage };
        })
      },
      {
        path: "search-cycles/missing",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/search-cycles-page");
          return { loader: module.searchCyclesLoader, Component: module.SearchCyclesPage };
        })
      },
      {
        path: "search-cycles/upgrades",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/search-cycles-page");
          return { loader: module.searchCyclesLoader, Component: module.SearchCyclesPage };
        })
      },
      {
        path: "search-cycles/failed-downloads",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/search-cycles-page");
          return { loader: module.searchCyclesLoader, Component: module.SearchCyclesPage };
        })
      },
      {
        path: "settings",
        element: <SettingsWorkspaceLayout />,
        children: [
          {
            index: true,
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-overview-page");
              return { loader: module.settingsOverviewLoader, Component: module.SettingsOverviewPage };
            })
          },
          {
            path: "libraries",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-libraries-page");
              return { loader: module.settingsLibrariesLoader, Component: module.SettingsLibrariesPage };
            })
          },
          {
            path: "media-management",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-media-management-page-v2");
              return {
                loader: module.settingsMediaManagementLoader,
                Component: module.SettingsMediaManagementPage
              };
            })
          },
          {
            path: "import-policy",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-media-management-page-v2");
              return {
                loader: module.settingsMediaManagementLoader,
                Component: module.SettingsMediaManagementPage
              };
            })
          },
          {
            path: "processing",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-media-management-page-v2");
              return {
                loader: module.settingsMediaManagementLoader,
                Component: module.SettingsMediaManagementPage
              };
            })
          },
          { path: "media", element: <Navigate to="/settings/media-management" replace /> },
          {
            path: "destination-rules",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-destination-rules-page");
              return {
                loader: module.settingsDestinationRulesLoader,
                Component: module.SettingsDestinationRulesPage
              };
            })
          },
          { path: "root-folders", element: <Navigate to="/settings/destination-rules" replace /> },
          {
            path: "policy-sets",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-policy-sets-page");
              return {
                loader: module.settingsPolicySetsLoader,
                Component: module.SettingsPolicySetsPage
              };
            })
          },
          {
            path: "profiles",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-profiles-page");
              return { loader: module.settingsProfilesLoader, Component: module.SettingsProfilesPage };
            })
          },
          {
            path: "quality",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-quality-page-v2");
              return { loader: module.settingsQualityLoader, Component: module.SettingsQualityPage };
            })
          },
          { path: "quality-sizes", element: <Navigate to="/settings/quality" replace /> },
          {
            path: "custom-formats",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-custom-formats-page");
              return {
                loader: module.settingsCustomFormatsLoader,
                Component: module.SettingsCustomFormatsPage
              };
            })
          },
          {
            path: "release-rules",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-release-rules-page");
              return {
                loader: module.settingsReleaseRulesLoader,
                Component: module.SettingsReleaseRulesPage
              };
            })
          },
          {
            path: "playback",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-playback-page");
              return {
                loader: module.settingsPlaybackLoader,
                Component: module.SettingsPlaybackPage
              };
            })
          },
          { path: "indexers", element: <Navigate to="/indexers/indexers" replace /> },
          { path: "download-clients", element: <Navigate to="/indexers/download-clients" replace /> },
          { path: "import-lists", element: <Navigate to="/settings/lists" replace /> },
          { path: "connect", element: <Navigate to="/indexers" replace /> },
          {
            path: "lists",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-lists-page");
              return { loader: module.settingsListsLoader, Component: module.SettingsListsPage };
            })
          },
          {
            path: "automation",
            element: <Navigate to="/search-cycles" replace />
          },
          {
            path: "migration",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-migration-page");
              return { Component: module.SettingsMigrationPage };
            })
          },
          {
            path: "metadata",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-metadata-page");
              return { loader: module.settingsMetadataLoader, Component: module.SettingsMetadataPage };
            })
          },
          {
            path: "tags",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-tags-page");
              return { loader: module.settingsTagsLoader, Component: module.SettingsTagsPage };
            })
          },
          {
            path: "general",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-general-page");
              return { loader: module.settingsGeneralLoader, Component: module.SettingsGeneralPage };
            })
          },
          {
            path: "notifications",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-notifications-page");
              return { loader: module.settingsNotificationsLoader, Component: module.SettingsNotificationsPage };
            })
          },
          {
            path: "ui",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/settings-ui-page");
              return { loader: module.settingsUiLoader, Component: module.SettingsUiPage };
            })
          },
          { path: "*", element: <Navigate to="/settings" replace /> }
        ]
      },
      {
        path: "system",
        element: <SystemWorkspaceLayout />,
        children: [
          {
            index: true,
            lazy: withSkeleton(async () => {
              const module = await import("./routes/system-page");
              return { loader: module.systemLoader, Component: module.SystemPage };
            })
          },
          {
            path: "audit",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/system-page");
              return { loader: module.systemLoader, Component: module.SystemPage };
            })
          },
          {
            path: "api",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/system-api-page");
              return { loader: module.systemApiLoader, Component: module.SystemApiPage };
            })
          },
          {
            path: "docs",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/system-docs-page");
              return { Component: module.SystemDocsPage };
            })
          },
          {
            path: "backups",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/system-page");
              return { loader: module.systemLoader, Component: module.SystemPage };
            })
          },
          {
            path: "recycle-bin",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/system-page");
              return { loader: module.systemLoader, Component: module.SystemPage };
            })
          },
          {
            path: "updates",
            lazy: withSkeleton(async () => {
              const module = await import("./routes/system-page");
              return { loader: module.systemLoader, Component: module.SystemPage };
            })
          }
        ]
      }
        ]
      }
    ]
  }
]);

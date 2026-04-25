import type { LoaderFunction } from "react-router-dom";
import { createBrowserRouter, Navigate, redirect, useParams } from "react-router-dom";
import type { ComponentType } from "react";
import { RouteErrorBoundary } from "./components/shell/route-error-boundary";
import { AppLayout } from "./layouts/app-layout";
import { LoginPage } from "./routes/login-page";
import { SetupPage } from "./routes/setup-page";
import { RouteSkeleton } from "./components/shell/skeleton";
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
    return {
      loader: mod.loader
        ? async (args: Parameters<NonNullable<LazyRouteModule["loader"]>>[0]) =>
            (await mod.loader!(args)) ?? null
        : async () => null,
      Component: mod.Component,
      ErrorBoundary: RouteErrorBoundary,
      HydrateFallback: RouteSkeleton
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
    throw redirect(returnTo || "/login");
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
        path: "movies",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/library-page");
          return { loader: module.moviesLoader, Component: module.MoviesPage };
        })
      },
      { path: "movies/library", element: <Navigate to="/movies" replace /> },
      {
        path: "movies/:id",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/movie-detail-page");
          return { loader: module.movieDetailLoader, Component: module.MovieDetailPage };
        })
      },
      {
        path: "tv",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/library-page");
          return { loader: module.showsLoader, Component: module.ShowsPage };
        })
      },
      { path: "tv/library", element: <Navigate to="/tv" replace /> },
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
        path: "indexers",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/indexers-page");
          return { loader: module.indexersLoader, Component: module.IndexersPage };
        })
      },
      {
        path: "settings",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-overview-page");
          return { loader: module.settingsOverviewLoader, Component: module.SettingsOverviewPage };
        })
      },
      {
        path: "settings/media-management",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-media-management-page-v2");
          return {
            loader: module.settingsMediaManagementLoader,
            Component: module.SettingsMediaManagementPage
          };
        })
      },
      { path: "settings/media", element: <Navigate to="/settings/media-management" replace /> },
      {
        path: "settings/destination-rules",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-destination-rules-page");
          return {
            loader: module.settingsDestinationRulesLoader,
            Component: module.SettingsDestinationRulesPage
          };
        })
      },
      { path: "settings/root-folders", element: <Navigate to="/settings/destination-rules" replace /> },
      {
        path: "settings/policy-sets",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-policy-sets-page");
          return {
            loader: module.settingsPolicySetsLoader,
            Component: module.SettingsPolicySetsPage
          };
        })
      },
      {
        path: "settings/profiles",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-profiles-page");
          return { loader: module.settingsProfilesLoader, Component: module.SettingsProfilesPage };
        })
      },
      {
        path: "settings/quality",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-quality-page-v2");
          return { loader: module.settingsQualityLoader, Component: module.SettingsQualityPage };
        })
      },
      { path: "settings/quality-sizes", element: <Navigate to="/settings/quality" replace /> },
      {
        path: "settings/custom-formats",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-custom-formats-page");
          return {
            loader: module.settingsCustomFormatsLoader,
            Component: module.SettingsCustomFormatsPage
          };
        })
      },
      { path: "settings/indexers", element: <Navigate to="/indexers" replace /> },
      { path: "settings/download-clients", element: <Navigate to="/indexers" replace /> },
      { path: "settings/import-lists", element: <Navigate to="/settings/lists" replace /> },
      { path: "settings/connect", element: <Navigate to="/settings/general" replace /> },
      {
        path: "settings/lists",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-lists-page");
          return { loader: module.settingsListsLoader, Component: module.SettingsListsPage };
        })
      },
      {
        path: "settings/metadata",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-metadata-page");
          return { loader: module.settingsMetadataLoader, Component: module.SettingsMetadataPage };
        })
      },
      {
        path: "settings/tags",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-tags-page");
          return { loader: module.settingsTagsLoader, Component: module.SettingsTagsPage };
        })
      },
      {
        path: "settings/general",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-general-page");
          return { loader: module.settingsGeneralLoader, Component: module.SettingsGeneralPage };
        })
      },
      {
        path: "settings/ui",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/settings-ui-page");
          return { loader: module.settingsUiLoader, Component: module.SettingsUiPage };
        })
      },
      { path: "settings/*", element: <Navigate to="/settings" replace /> },
      {
        path: "system",
        lazy: withSkeleton(async () => {
          const module = await import("./routes/system-page");
          return { loader: module.systemLoader, Component: module.SystemPage };
        })
      }
    ]
  }
]);

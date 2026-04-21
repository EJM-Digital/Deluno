import { createBrowserRouter } from "react-router-dom";
import { RootLayout } from "./shell/root-layout";
import {
  DashboardPage,
  dashboardLoader
} from "./routes/dashboard-page";
import {
  MoviesPage,
  moviesAction,
  moviesLoader
} from "./routes/movies-page";
import {
  SeriesPage,
  seriesAction,
  seriesLoader
} from "./routes/series-page";
import {
  SettingsPage,
  settingsAction,
  settingsLoader
} from "./routes/settings-page";
import { ActivityPage, activityLoader } from "./routes/activity-page";
import { LibrariesPage } from "./routes/libraries-page";
import { ConnectionsPage } from "./routes/connections-page";

export const router = createBrowserRouter([
  {
    path: "/",
    element: <RootLayout />,
    children: [
      {
        index: true,
        loader: dashboardLoader,
        element: <DashboardPage />
      },
      {
        path: "movies",
        loader: moviesLoader,
        action: moviesAction,
        element: <MoviesPage />
      },
      {
        path: "series",
        loader: seriesLoader,
        action: seriesAction,
        element: <SeriesPage />
      },
      {
        path: "activity",
        loader: activityLoader,
        element: <ActivityPage />
      },
      {
        path: "libraries",
        element: <LibrariesPage />
      },
      {
        path: "connections",
        element: <ConnectionsPage />
      },
      {
        path: "settings",
        loader: settingsLoader,
        action: settingsAction,
        element: <SettingsPage />
      }
    ]
  }
]);

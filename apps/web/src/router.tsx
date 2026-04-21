import { createBrowserRouter } from "react-router-dom";
import { RootLayout } from "./shell/root-layout";
import { ActivityPage } from "./routes/activity-page";
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
import { SettingsPage } from "./routes/settings-page";

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
        element: <ActivityPage />
      },
      {
        path: "settings",
        element: <SettingsPage />
      }
    ]
  }
]);

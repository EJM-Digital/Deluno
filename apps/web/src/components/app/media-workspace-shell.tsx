import { Outlet } from "react-router-dom";

/**
 * Movies and TV use one visible library workspace. Status, upgrade, and
 * recovery states are filters in that workspace—not hidden sub-apps.
 * The shell remains so legacy deep links can redirect safely in the router.
 */
export function MoviesWorkspaceLayout() {
  return <Outlet />;
}

export function TvWorkspaceLayout() {
  return <Outlet />;
}

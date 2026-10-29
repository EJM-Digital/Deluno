/**
 * Transfers — one page for what Transfers and Activity showed apart (#1426).
 *
 * Three views, chosen by the chips in the header and kept in the address (`?view=`):
 *   - Live downloads (the default): what is downloading now, what needs a person, and the cleanup guardrails.
 *   - Hand-offs: what is with the processor and what is being imported.
 *   - History & events: the record of what happened to each title, with its status chips and filters.
 * The old Activity address opens the history, keeping everything it carried.
 */
import { Navigate, useLocation, useSearchParams } from "react-router-dom";
import { transfersViewFromParams, legacyActivityTarget } from "../lib/transfers-view";
import { ActivityPage, activityLoader } from "./activity-page";
import { QueuePage as LiveTransfersPage, queueLoader as liveTransfersLoader } from "./queue-screen";

type LoaderArgs = { request?: Request } | undefined;

/** Starts only the view's own reads: the history does not wait for the downloads, nor the downloads for the history. */
export function transfersLoader(args?: LoaderArgs) {
  const search = args?.request ? new URL(args.request.url).searchParams : new URLSearchParams();
  return transfersViewFromParams(search) === "history" ? activityLoader(args) : liveTransfersLoader(args);
}

export function TransfersPage() {
  const [searchParams] = useSearchParams();
  return transfersViewFromParams(searchParams) === "history" ? <ActivityPage /> : <LiveTransfersPage />;
}

/** `/activity` and its query strings: the history, kept as it was asked for. */
export function LegacyActivityRedirect() {
  const { search } = useLocation();
  return <Navigate to={legacyActivityTarget(search)} replace />;
}

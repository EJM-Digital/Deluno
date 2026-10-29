/**
 * The views of the one Transfers page (#1426): live downloads, the hand-offs to the processor and the
 * imports, and the history of what happened. The view is in the address (`/queue?view=history&status=failed`),
 * so a link lands on it; no `view` is the live downloads. The old Activity address (`/activity?...`) opens
 * the history with its own parameters kept.
 */
export type TransfersView = "live" | "handoffs" | "history";

export const TRANSFERS_VIEWS: readonly { value: TransfersView; label: string }[] = [
  { value: "live", label: "Live downloads" },
  { value: "handoffs", label: "Hand-offs" },
  { value: "history", label: "History & events" }
];

export function transfersViewFromParams(params: URLSearchParams): TransfersView {
  const view = params.get("view");
  return view === "handoffs" || view === "history" ? view : "live";
}

/** The address with this view chosen. Choosing a view drops the other views' filters; live is the absence of a view. */
export function paramsWithView(view: TransfersView): URLSearchParams {
  const params = new URLSearchParams();
  if (view !== "live") params.set("view", view);
  return params;
}

/** The address of the history for these parameters (an Activity link's query, `view` put first so the link reads as it is said). */
export function historyPath(query: string | URLSearchParams = ""): string {
  const rest = new URLSearchParams(query);
  rest.delete("view");
  const text = rest.toString();
  return text ? `/queue?view=history&${text}` : "/queue?view=history";
}

/** Where the old `/activity?...` address goes: the history, keeping every parameter it carried. */
export function legacyActivityTarget(search: string): string {
  return historyPath(search.replace(/^\?/, ""));
}

/** The link to the indexer a release came from: the Indexers list, with that indexer's drawer open. */
export function indexerPageLink(indexerName: string): string {
  return `/indexers/indexers?${new URLSearchParams({ indexer: indexerName }).toString()}`;
}

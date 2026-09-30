import type { ReactNode } from "react";
import { AlertTriangle, RefreshCw } from "lucide-react";
import { Button } from "../ui/button";
import type { RouteSourceState, RouteSources } from "../../lib/route-cache";
import { RouteSkeleton } from "./skeleton";

/**
 * What a screen shows while its data arrives, and when part of it cannot (#1081).
 *
 * The screen never waits on its slowest request and never falls to the error
 * page: until what it cannot draw without has arrived it shows its own shape in
 * place, and when a source fails it says which one, in the screen, with a Retry.
 */
export function RouteSourceGate<S extends RouteSources>({
  state,
  children
}: {
  state: RouteSourceState<S>;
  children: () => ReactNode;
}) {
  if (state.blocked) {
    return (
      <SourceProblems
        state={state}
        className="mx-auto mt-6 max-w-2xl"
        intro="This screen could not load everything it needs."
      />
    );
  }

  if (!state.ready) return <RouteSkeleton />;

  return (
    <>
      {state.failed.length > 0 ? <SourceProblems state={state} className="mb-3" /> : null}
      {children()}
    </>
  );
}

function SourceProblems<S extends RouteSources>({
  state,
  className,
  intro
}: {
  state: RouteSourceState<S>;
  className?: string;
  intro?: string;
}) {
  const names = state.failed.map((item) => item.label);
  const [first] = state.failed;
  return (
    <div
      role="alert"
      className={`flex flex-wrap items-center gap-3 rounded-xl border border-warning/40 bg-warning/10 px-4 py-3 text-sm ${className ?? ""}`}
    >
      <AlertTriangle aria-hidden="true" className="h-4 w-4 shrink-0 text-warning" />
      <p className="min-w-0 flex-1 text-foreground">
        {intro ? `${intro} ` : ""}
        {names.length === 1 ? `${names[0]} could not be read.` : `${names.join(", ")} could not be read.`}
        {first?.message ? ` ${first.message}` : ""}
      </p>
      <Button type="button" variant="outline" size="sm" onClick={() => void state.refetch()}>
        <RefreshCw aria-hidden="true" className="h-3.5 w-3.5" />
        Retry
      </Button>
    </div>
  );
}

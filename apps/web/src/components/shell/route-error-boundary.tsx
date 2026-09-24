import { useEffect } from "react";
import { isRouteErrorResponse, useNavigate, useRouteError } from "react-router-dom";
import { isStaleBuildError, reloadOntoNewBuild } from "../../lib/stale-build";
import { ErrorState } from "./error-state";

export function RouteErrorBoundary() {
  const error = useRouteError();
  const navigate = useNavigate();
  const staleBuild = isStaleBuildError(error);

  // A tab from before an update asked for a screen the update replaced (#622).
  useEffect(() => {
    if (staleBuild) {
      reloadOntoNewBuild();
    }
  }, [staleBuild]);

  if (staleBuild) {
    return (
      <ErrorState
        title="Deluno was updated"
        message="This tab was opened before the update, so it asked for a screen the update replaced. Reload to open the new version."
        code="route:stale-build"
        onRetry={() => window.location.reload()}
        onReport={() => navigate("/system")}
      />
    );
  }

  if (isRouteErrorResponse(error)) {
    return (
      <ErrorState
        title={error.status === 404 ? "That page is missing" : "This area could not load"}
        message={
          error.status === 404
            ? "The route is not available in this Deluno build."
            : error.statusText || "The route loader failed before Deluno could render the page."
        }
        code={`route:${error.status}`}
        onRetry={() => window.location.reload()}
        onReport={() => navigate("/system")}
      />
    );
  }

  const message =
    error instanceof Error ? error.message : "The route failed before Deluno could render the page.";

  return (
    <ErrorState
      title="This area could not load"
      message={message}
      code="route:unexpected"
      onRetry={() => window.location.reload()}
      onReport={() => navigate("/system")}
    />
  );
}

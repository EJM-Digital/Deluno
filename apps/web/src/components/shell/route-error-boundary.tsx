import { isRouteErrorResponse, useNavigate, useRouteError } from "react-router-dom";
import { ErrorState } from "./error-state";

export function RouteErrorBoundary() {
  const error = useRouteError();
  const navigate = useNavigate();

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

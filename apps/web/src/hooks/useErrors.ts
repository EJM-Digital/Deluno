import { useState, useCallback } from "react";

export interface AppError {
  code: string;
  message: string;
  severity: "info" | "warning" | "error" | "critical";
  isRetryable?: boolean;
  recoverySuggestions?: string[];
  traceId?: string;
  occurredAt?: string;
}

/**
 * Hook for managing application errors with add, remove, and clear operations.
 * Provides a clean API for error state management in React components.
 */
export function useErrors() {
  const [errors, setErrors] = useState<AppError[]>([]);

  const addError = useCallback((error: AppError) => {
    setErrors((prev) => [...prev, error]);
  }, []);

  const removeError = useCallback((code: string) => {
    setErrors((prev) => prev.filter((e) => e.code !== code));
  }, []);

  const clearErrors = useCallback(() => {
    setErrors([]);
  }, []);

  const addErrorFromResponse = useCallback(
    (response: {
      code?: string;
      message?: string;
      severity?: string;
      isRetryable?: boolean;
      recoverySuggestions?: string[];
      traceId?: string;
      occurredAt?: string;
    }) => {
      const error: AppError = {
        code: response.code || "UNKNOWN_ERROR",
        message: response.message || "An unexpected error occurred",
        severity: (response.severity as any) || "error",
        isRetryable: response.isRetryable,
        recoverySuggestions: response.recoverySuggestions,
        traceId: response.traceId,
        occurredAt: response.occurredAt,
      };
      addError(error);
    },
    [addError]
  );

  const handleFetchError = useCallback(
    (error: unknown) => {
      if (error instanceof Error) {
        addError({
          code: "NETWORK_ERROR",
          message: error.message || "Network request failed",
          severity: "error",
          isRetryable: true,
          recoverySuggestions: [
            "Check your internet connection",
            "Try again in a moment",
            "Contact support if the problem persists",
          ],
        });
      } else {
        addError({
          code: "UNKNOWN_ERROR",
          message: "An unexpected error occurred",
          severity: "error",
          recoverySuggestions: ["Try again or contact support"],
        });
      }
    },
    [addError]
  );

  return {
    errors,
    addError,
    removeError,
    clearErrors,
    addErrorFromResponse,
    handleFetchError,
    hasErrors: errors.length > 0,
    criticalErrors: errors.filter((e) => e.severity === "critical"),
  };
}

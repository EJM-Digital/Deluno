import { useCallback, useState } from "react";
import { useRevalidator } from "react-router-dom";
import { ApiRequestError, readValidationProblem } from "./api";
import { authedFetch } from "./use-auth";

export type ApiMutationState = "idle" | "saving" | "saved" | "error";

export function useApiMutation<TReq, TRes>(path: string, method: string) {
  const revalidator = useRevalidator();
  const [state, setState] = useState<ApiMutationState>("idle");
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  const mutate = useCallback(
    async (request: TReq): Promise<TRes> => {
      setState("saving");
      setFieldErrors({});

      try {
        const response = await authedFetch(path, {
          method,
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(request)
        });

        if (!response.ok) {
          const problem = await readValidationProblem(response.clone());
          const errors = problem?.errors ?? {};
          setFieldErrors(Object.fromEntries(Object.entries(errors).map(([key, messages]) => [key, messages.join(" ")])));
          const detail = Object.values(errors).flat()[0] ?? problem?.title ?? `Request failed with status ${response.status}.`;
          throw new ApiRequestError(detail, response.status, path, JSON.stringify(problem ?? {}));
        }

        const result = response.status === 204 ? (undefined as TRes) : ((await response.json()) as TRes);
        setState("saved");
        revalidator.revalidate();
        return result;
      } catch (error) {
        setState("error");
        throw error;
      }
    },
    [method, path, revalidator]
  );

  return { mutate, state, fieldErrors };
}

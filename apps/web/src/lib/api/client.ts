import { authedFetch } from "../use-auth";
import type { ApiPage, ValidationProblem } from "./types";

export class ApiRequestError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly path: string,
    public readonly responseBody: string
  ) {
    super(message);
    this.name = "ApiRequestError";
  }
}

const TRANSIENT_HTTP_STATUSES = new Set([408, 425, 429, 500, 502, 503, 504]);
const RETRY_DELAYS_MS = [150, 450, 1_000] as const;

function canRetryRequest(init?: RequestInit) {
  const method = (init?.method ?? "GET").toUpperCase();
  return method === "GET" || method === "HEAD" || method === "OPTIONS";
}

function isTransientNetworkError(error: unknown) {
  return error instanceof TypeError || (typeof DOMException !== "undefined" && error instanceof DOMException && error.name !== "AbortError");
}

function waitForRetry(attempt: number) {
  return new Promise<void>((resolve) => globalThis.setTimeout(resolve, RETRY_DELAYS_MS[attempt] ?? RETRY_DELAYS_MS.at(-1)!));
}

export async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const requestInit: RequestInit = { cache: "no-store", ...init };
  const retryable = canRetryRequest(requestInit);
  let response: Response;

  for (let attempt = 0; ; attempt++) {
    try {
      response = await authedFetch(path, requestInit);
    } catch (error) {
      if (!retryable || attempt >= RETRY_DELAYS_MS.length || !isTransientNetworkError(error)) throw error;
      await waitForRetry(attempt);
      continue;
    }

    if (response.ok || !retryable || !TRANSIENT_HTTP_STATUSES.has(response.status) || attempt >= RETRY_DELAYS_MS.length) {
      break;
    }

    await waitForRetry(attempt);
  }

  if (!response.ok) {
    const responseBody = await response.text().catch(() => "");
    let message = `Request failed for ${path} with status ${response.status}.`;

    if (responseBody) {
      try {
        const parsed = JSON.parse(responseBody) as { message?: unknown; title?: unknown };
        const serverMessage =
          typeof parsed.message === "string"
            ? parsed.message
            : typeof parsed.title === "string"
              ? parsed.title
              : null;

        if (serverMessage) {
          message = serverMessage;
        }
      } catch {
        message = responseBody;
      }
    }

    throw new ApiRequestError(message, response.status, path, responseBody);
  }

  return (await response.json()) as T;
}

/** Reads one explicit operational-list page, including its continuation signal. */
export async function fetchPage<T>(path: string, init?: RequestInit): Promise<ApiPage<T>> {
  return fetchJson<ApiPage<T>>(path, init);
}

/** Reads every page deliberately, for callers whose result is a complete count rather than a screen window. */
export async function fetchAllPages<T>(path: string, init?: RequestInit): Promise<T[]> {
  const items: T[] = [];
  const seenTokens = new Set<string>();
  let nextPath = path;

  while (true) {
    const page = await fetchPage<T>(nextPath, init);
    items.push(...page.items);

    if (!page.hasMore || !page.nextPageToken) {
      return items;
    }

    if (seenTokens.has(page.nextPageToken)) {
      throw new Error("The API returned a repeated page token.");
    }

    seenTokens.add(page.nextPageToken);
    const separator = path.includes("?") ? "&" : "?";
    nextPath = `${path}${separator}pageToken=${encodeURIComponent(page.nextPageToken)}`;
  }
}

/** Reads one explicit operational-list page for screens that only render their current window. */
export async function fetchPageItems<T>(path: string, init?: RequestInit): Promise<T[]> {
  return (await fetchPage<T>(path, init)).items;
}

export async function readValidationProblem(
  response: Response
): Promise<ValidationProblem | null> {
  try {
    return (await response.json()) as ValidationProblem;
  } catch {
    return null;
  }
}

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

/**
 * The sentence to put in front of the user when a request fails.
 *
 * ASP.NET answers a rejected request with a ProblemDetails body whose `title`
 * is the boilerplate "One or more validation errors occurred." and whose
 * `errors` carries the reason. Reading `title` first meant every refusal in
 * Deluno arrived as that sentence, and the thing the server had gone to the
 * trouble of saying - which field, and what to do about it - was thrown away.
 *
 * So the specific beats the general: an explicit `message`, then the field
 * errors, then `detail`, and only then the boilerplate title.
 */
export function readErrorMessage(responseBody: string, path: string, status: number): string {
  const fallback = `Request failed for ${path} with status ${status}.`;
  if (!responseBody) return fallback;

  let parsed: { message?: unknown; title?: unknown; detail?: unknown; errors?: unknown };
  try {
    parsed = JSON.parse(responseBody) as typeof parsed;
  } catch {
    return responseBody;
  }

  if (typeof parsed.message === "string" && parsed.message.trim()) return parsed.message;

  if (parsed.errors && typeof parsed.errors === "object") {
    const reasons = Object.values(parsed.errors as Record<string, unknown>)
      .flatMap((value) => (Array.isArray(value) ? value : [value]))
      .filter((value): value is string => typeof value === "string" && value.trim().length > 0);

    // Several fields can be wrong at once, and naming one of them would be
    // worse than naming none.
    if (reasons.length > 0) return reasons.join(" ");
  }

  if (typeof parsed.detail === "string" && parsed.detail.trim()) return parsed.detail;
  if (typeof parsed.title === "string" && parsed.title.trim()) return parsed.title;

  return fallback;
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
    throw new ApiRequestError(
      readErrorMessage(responseBody, path, response.status),
      response.status,
      path,
      responseBody);
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

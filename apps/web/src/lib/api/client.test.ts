import { describe, expect, it, vi } from "vitest";

vi.mock("../use-auth", () => ({ authedFetch: vi.fn() }));

const { authedFetch } = await import("../use-auth");
const { fetchJson, fetchPage, fetchAllPages, fetchPageItems, readValidationProblem, readErrorMessage, readErrorText, ApiRequestError } = await import("./client");

/**
 * Deluno refuses things for good reasons and then has to say them. A refusal
 * the user cannot act on is barely better than the silent wrong answer it
 * replaced, which is how this got noticed: #459 turned "a key with no scope
 * gets every scope" into a validation error, and the error arrived on screen as
 * "One or more validation errors occurred."
 */
describe("readErrorMessage", () => {
  const path = "/api/api-keys";

  it("prefers the field reason over ASP.NET's boilerplate title", () => {
    const body = JSON.stringify({
      type: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
      title: "One or more validation errors occurred.",
      status: 400,
      errors: {
        scopes: ['Say what this key may do. Use a template (dashboard-read, automation) or a scope list.']
      }
    });

    expect(readErrorMessage(body, path, 400)).toContain("Say what this key may do");
    expect(readErrorMessage(body, path, 400)).not.toContain("One or more validation errors");
  });

  it("names every field that is wrong, because naming one would mislead", () => {
    const body = JSON.stringify({
      title: "One or more validation errors occurred.",
      errors: { name: ["Give this API key a clear name."], scopes: ["Say what this key may do."] }
    });

    const message = readErrorMessage(body, path, 400);

    expect(message).toContain("Give this API key a clear name.");
    expect(message).toContain("Say what this key may do.");
  });

  it("still prefers an explicit message when the server sends one", () => {
    const body = JSON.stringify({ message: "SABnzbd rejected authentication with 403.", title: "Bad Request" });

    expect(readErrorMessage(body, path, 400)).toBe("SABnzbd rejected authentication with 403.");
  });

  it("falls back through detail to title", () => {
    expect(readErrorMessage(JSON.stringify({ detail: "The library root is gone.", title: "Not Found" }), path, 404))
      .toBe("The library root is gone.");
    expect(readErrorMessage(JSON.stringify({ title: "Not Found" }), path, 404)).toBe("Not Found");
  });

  it("says something useful when the body is empty, unparseable, or says nothing", () => {
    expect(readErrorMessage("", path, 500)).toBe("Request failed for /api/api-keys with status 500.");
    expect(readErrorMessage("<html>502 Bad Gateway</html>", path, 502)).toBe("<html>502 Bad Gateway</html>");
    expect(readErrorMessage(JSON.stringify({ status: 500 }), path, 500))
      .toBe("Request failed for /api/api-keys with status 500.");
  });

  it("ignores blank and non-string reasons rather than showing an empty toast", () => {
    const body = JSON.stringify({ message: "   ", errors: { scopes: ["", null, 42] }, title: "Bad Request" });

    expect(readErrorMessage(body, path, 400)).toBe("Bad Request");
  });
});


/**
 * The same problem one layer out. Screens that call fetch themselves were
 * putting the raw body into an Error, so a refusal arrived as a whole
 * problem+json document. Seen on the rig: refusing a library whose root is a
 * file rendered the entire JSON blob to the owner, braces and type URL and all,
 * with the one useful sentence buried inside it.
 */
describe("readErrorText", () => {
  const asResponse = (body: string, status = 400) =>
    ({ status, url: "http://localhost/api/libraries", text: async () => body }) as unknown as Response;

  it("pulls the sentence out of a validation problem instead of showing the document", async () => {
    const reason = "That is a file, not a folder. Choose the folder this library lives in.";
    const body = JSON.stringify({
      type: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
      title: "One or more validation errors occurred.",
      status: 400,
      errors: { rootPath: [reason] }
    });

    await expect(readErrorText(asResponse(body), "Library could not be created.")).resolves.toBe(reason);
  });

  it("uses the caller's sentence when the body is empty", async () => {
    // The old shape was response.text().catch(() => fallback), and .catch only
    // runs when reading the body throws - which is close to never. An empty
    // body produced an empty message, so the fallback was unreachable.
    await expect(readErrorText(asResponse(""), "Library could not be created."))
      .resolves.toBe("Library could not be created.");
    await expect(readErrorText(asResponse("   "), "Library could not be created."))
      .resolves.toBe("Library could not be created.");
  });

  it("prefers the caller's sentence over the generic path-and-status line", async () => {
    await expect(readErrorText(asResponse(JSON.stringify({ status: 500 }), 500), "Search could not be requested."))
      .resolves.toBe("Search could not be requested.");
  });

  it("passes a plain-text body straight through", async () => {
    await expect(readErrorText(asResponse("Upstream timed out.", 504), "Search could not be requested."))
      .resolves.toBe("Upstream timed out.");
  });
});

describe("fetchJson retries", () => {
  const busy = () => new Response(JSON.stringify({ message: "Busy." }), { status: 503 });

  it("tries a busy GET again and answers once it is through", async () => {
    vi.mocked(authedFetch).mockReset().mockResolvedValueOnce(busy()).mockResolvedValueOnce(new Response("[1]", { status: 200 }));
    await expect(fetchJson<number[]>("/api/x")).resolves.toEqual([1]);
    expect(authedFetch).toHaveBeenCalledTimes(2);
  });

  it("tries again after a network failure when the caller gave no way to stop it", async () => {
    vi.mocked(authedFetch).mockReset().mockRejectedValueOnce(new TypeError("Failed to fetch")).mockResolvedValueOnce(new Response("[2]", { status: 200 }));
    await expect(fetchJson<number[]>("/api/x")).resolves.toEqual([2]);
    expect(authedFetch).toHaveBeenCalledTimes(2);
  });

  it("stops at once when the caller gives up while it waits to try again (#1396)", async () => {
    vi.mocked(authedFetch).mockReset().mockImplementation(async () => busy());
    const caller = new AbortController();
    const started = Date.now();
    const answer = fetchJson("/api/x", { signal: caller.signal });
    await vi.waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    caller.abort();
    await expect(answer).rejects.toThrow();
    expect(Date.now() - started).toBeLessThan(140);
    expect(authedFetch).toHaveBeenCalledTimes(1);
  });

  it("stops at once when the caller gives up while a network failure waits to be tried again", async () => {
    vi.mocked(authedFetch).mockReset().mockRejectedValue(new TypeError("Failed to fetch"));
    const caller = new AbortController();
    const answer = fetchJson("/api/x", { signal: caller.signal });
    await vi.waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    caller.abort();
    await expect(answer).rejects.toThrow();
    expect(authedFetch).toHaveBeenCalledTimes(1);
  });
});

describe("readErrorMessage edge cases", () => {
  const path = "/api/x";

  it("joins several reasons with a single space", () => {
    const body = JSON.stringify({ errors: { a: ["One."], b: ["Two."] } });
    expect(readErrorMessage(body, path, 400)).toBe("One. Two.");
  });

  it("accepts a field whose reason is a single sentence rather than a list", () => {
    expect(readErrorMessage(JSON.stringify({ errors: { a: "Single." } }), path, 400)).toBe("Single.");
  });

  it("ignores reasons that are only spaces", () => {
    const body = JSON.stringify({ errors: { a: ["   ", "Real reason."] } });
    expect(readErrorMessage(body, path, 400)).toBe("Real reason.");
  });

  it("does not read letters out of an errors field that is plain text or empty", () => {
    expect(readErrorMessage(JSON.stringify({ errors: "bad", title: "Title" }), path, 400)).toBe("Title");
    expect(readErrorMessage(JSON.stringify({ errors: null, title: "Title" }), path, 400)).toBe("Title");
  });

  it("skips a detail or title that is only spaces", () => {
    expect(readErrorMessage(JSON.stringify({ detail: "  ", title: "Title" }), path, 400)).toBe("Title");
    expect(readErrorMessage(JSON.stringify({ detail: "  ", title: "  " }), path, 400)).toBe("Request failed for /api/x with status 400.");
  });
});

describe("readErrorText when the body cannot be read", () => {
  it("uses the caller's sentence", async () => {
    const response = { status: 500, url: "http://localhost/x", text: async () => { throw new Error("gone"); } } as unknown as Response;
    await expect(readErrorText(response, "Could not save.")).resolves.toBe("Could not save.");
  });
});

describe("fetchJson timing and limits", () => {
  const busy = () => new Response("", { status: 503 });
  const ok = () => new Response("[1]", { status: 200 });

  async function settle() {
    for (let i = 0; i < 5; i++) await Promise.resolve();
  }

  it("waits 150, 450 and 1000 milliseconds between tries, then gives up with the last answer", async () => {
    vi.useFakeTimers();
    try {
      vi.mocked(authedFetch).mockReset().mockImplementation(async () => busy());
      const answer = fetchJson("/api/x").catch((error: unknown) => error);
      await settle();
      expect(authedFetch).toHaveBeenCalledTimes(1);
      await vi.advanceTimersByTimeAsync(149);
      expect(authedFetch).toHaveBeenCalledTimes(1);
      await vi.advanceTimersByTimeAsync(1);
      expect(authedFetch).toHaveBeenCalledTimes(2);
      await vi.advanceTimersByTimeAsync(449);
      expect(authedFetch).toHaveBeenCalledTimes(2);
      await vi.advanceTimersByTimeAsync(1);
      expect(authedFetch).toHaveBeenCalledTimes(3);
      await vi.advanceTimersByTimeAsync(999);
      expect(authedFetch).toHaveBeenCalledTimes(3);
      await vi.advanceTimersByTimeAsync(1);
      const error = await answer;
      expect(authedFetch).toHaveBeenCalledTimes(4);
      expect(error).toBeInstanceOf(ApiRequestError);
      expect((error as InstanceType<typeof ApiRequestError>).status).toBe(503);
    } finally {
      vi.useRealTimers();
    }
  });

  it("gives up after four network failures and throws the failure", async () => {
    vi.useFakeTimers();
    try {
      const failure = new TypeError("Failed to fetch");
      vi.mocked(authedFetch).mockReset().mockRejectedValue(failure);
      const answer = fetchJson("/api/x").catch((error: unknown) => error);
      await vi.advanceTimersByTimeAsync(2_000);
      await expect(answer).resolves.toBe(failure);
      expect(authedFetch).toHaveBeenCalledTimes(4);
    } finally {
      vi.useRealTimers();
    }
  });

  it("retries a browser network error that is not an abort", async () => {
    vi.mocked(authedFetch).mockReset()
      .mockRejectedValueOnce(new DOMException("net", "NetworkError"))
      .mockResolvedValueOnce(ok());
    await expect(fetchJson<number[]>("/api/x")).resolves.toEqual([1]);
    expect(authedFetch).toHaveBeenCalledTimes(2);
  });

  it("does not retry an abort or any other kind of error", async () => {
    const abort = new DOMException("stop", "AbortError");
    vi.mocked(authedFetch).mockReset().mockRejectedValue(abort);
    await expect(fetchJson("/api/x")).rejects.toBe(abort);
    expect(authedFetch).toHaveBeenCalledTimes(1);

    const other = new Error("boom");
    vi.mocked(authedFetch).mockReset().mockRejectedValue(other);
    await expect(fetchJson("/api/x")).rejects.toBe(other);
    expect(authedFetch).toHaveBeenCalledTimes(1);
  });

  it("does not retry a request that changes something", async () => {
    vi.mocked(authedFetch).mockReset().mockImplementation(async () => busy());
    await expect(fetchJson("/api/x", { method: "POST" })).rejects.toBeInstanceOf(ApiRequestError);
    expect(authedFetch).toHaveBeenCalledTimes(1);

    const failure = new TypeError("Failed to fetch");
    vi.mocked(authedFetch).mockReset().mockRejectedValue(failure);
    await expect(fetchJson("/api/x", { method: "DELETE" })).rejects.toBe(failure);
    expect(authedFetch).toHaveBeenCalledTimes(1);
  });

  it("retries HEAD, OPTIONS and lower-case get, which only read", async () => {
    for (const method of ["HEAD", "OPTIONS", "get"]) {
      vi.mocked(authedFetch).mockReset().mockResolvedValueOnce(busy()).mockResolvedValueOnce(ok());
      await expect(fetchJson("/api/x", { method })).resolves.toEqual([1]);
      expect(authedFetch).toHaveBeenCalledTimes(2);
    }
  });

  it("does not retry an answer that is a plain refusal", async () => {
    vi.mocked(authedFetch).mockReset().mockResolvedValue(new Response(JSON.stringify({ message: "No." }), { status: 404 }));
    await expect(fetchJson("/api/x")).rejects.toMatchObject({ name: "ApiRequestError", message: "No.", status: 404, path: "/api/x", responseBody: JSON.stringify({ message: "No." }) });
    expect(authedFetch).toHaveBeenCalledTimes(1);
  });

  it("keeps the answer out of the cache and lets the caller's options win", async () => {
    vi.mocked(authedFetch).mockReset().mockImplementation(async () => ok());
    await fetchJson("/api/x");
    expect(authedFetch).toHaveBeenLastCalledWith("/api/x", { cache: "no-store" });
    await fetchJson("/api/x", { cache: "reload" });
    expect(authedFetch).toHaveBeenLastCalledWith("/api/x", { cache: "reload" });
  });

  it("still reports a refusal whose body cannot be read", async () => {
    const response = { ok: false, status: 400, text: () => Promise.reject(new Error("gone")) } as unknown as Response;
    vi.mocked(authedFetch).mockReset().mockResolvedValue(response);
    await expect(fetchJson("/api/x", { method: "POST" })).rejects.toMatchObject({
      message: "Request failed for /api/x with status 400.",
      responseBody: ""
    });
  });
});

describe("paged reads", () => {
  const page = (items: number[], next?: string) =>
    new Response(JSON.stringify({ items, hasMore: Boolean(next), nextPageToken: next ?? null }), { status: 200 });

  it("fetchPage and fetchPageItems return the page and its items", async () => {
    vi.mocked(authedFetch).mockReset().mockImplementation(async () => page([1, 2]));
    await expect(fetchPage("/api/p")).resolves.toMatchObject({ items: [1, 2], hasMore: false });
    await expect(fetchPageItems("/api/p")).resolves.toEqual([1, 2]);
  });

  it("fetchAllPages follows the token with ? or & as the path needs", async () => {
    vi.mocked(authedFetch).mockReset()
      .mockResolvedValueOnce(page([1], "a b"))
      .mockResolvedValueOnce(page([2]));
    await expect(fetchAllPages("/api/p")).resolves.toEqual([1, 2]);
    expect(vi.mocked(authedFetch).mock.calls[1][0]).toBe("/api/p?pageToken=a%20b");

    vi.mocked(authedFetch).mockReset()
      .mockResolvedValueOnce(page([1], "t"))
      .mockResolvedValueOnce(page([2]));
    await fetchAllPages("/api/p?x=1");
    expect(vi.mocked(authedFetch).mock.calls[1][0]).toBe("/api/p?x=1&pageToken=t");
  });

  it("fetchAllPages stops when a page says there is more but gives no token", async () => {
    vi.mocked(authedFetch).mockReset().mockImplementation(async () =>
      new Response(JSON.stringify({ items: [1], hasMore: true, nextPageToken: null }), { status: 200 }));
    await expect(fetchAllPages("/api/p")).resolves.toEqual([1]);
    expect(authedFetch).toHaveBeenCalledTimes(1);
  });

  it("fetchAllPages stops when a token comes back twice", async () => {
    vi.mocked(authedFetch).mockReset().mockImplementation(async () => page([1], "same"));
    await expect(fetchAllPages("/api/p")).rejects.toThrow("The API returned a repeated page token.");
    expect(authedFetch).toHaveBeenCalledTimes(2);
  });
});

describe("readValidationProblem", () => {
  it("returns the problem, or null when the body is not JSON", async () => {
    await expect(readValidationProblem(new Response(JSON.stringify({ title: "Bad" })))).resolves.toEqual({ title: "Bad" });
    await expect(readValidationProblem(new Response("<html>"))).resolves.toBeNull();
  });
});

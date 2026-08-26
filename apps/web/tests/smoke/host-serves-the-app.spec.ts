import { expect, test } from "@playwright/test";

/**
 * The rest of the smoke suite loads the UI from `serve-smoke-preview.mjs`, which does
 * not exist in a real install — there, Deluno.Host serves its own static files. That
 * gap let a bug ship: the SPA fallback sent index.html with no Content-Type, and
 * because Deluno also sends `X-Content-Type-Options: nosniff`, the browser was told
 * not to guess and rendered the whole app as raw source.
 *
 * These hit the host directly, which is the only way to catch that class of problem.
 */

const API_ORIGIN = process.env.DELUNO_API_ORIGIN ?? "http://127.0.0.1:5199";

test.describe("the host serves the app itself", () => {
  test("the root is served as HTML a browser will render", async ({ request }) => {
    const response = await request.get(`${API_ORIGIN}/`);

    expect(response.status()).toBe(200);
    // Without this header, nosniff makes Chrome render the markup as plain text.
    expect(response.headers()["content-type"] ?? "").toContain("text/html");
    expect(await response.text()).toContain("<div id=\"root\">");
  });

  test("a client-side route is served the same way", async ({ request }) => {
    // Deep links all land on the SPA fallback, so they need the header just as much.
    const response = await request.get(`${API_ORIGIN}/settings`);

    expect(response.status()).toBe(200);
    expect(response.headers()["content-type"] ?? "").toContain("text/html");
  });

  test("an unknown API path is still a 404, not the app shell", async ({ request }) => {
    // The fallback must not swallow API 404s and hand back index.html, or a broken
    // client call looks like a successful page load.
    const response = await request.get(`${API_ORIGIN}/api/definitely-not-a-route`);

    expect(response.status()).toBe(404);
    expect(await response.text()).not.toContain("<div id=\"root\">");
  });
});

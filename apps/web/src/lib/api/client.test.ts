import { describe, expect, it } from "vitest";

import { readErrorMessage } from "./client";

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

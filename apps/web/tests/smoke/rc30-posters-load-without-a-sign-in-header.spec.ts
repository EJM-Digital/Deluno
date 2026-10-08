import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// rc.30 live test, #1146. Every poster was blank because the artwork address
// answered 401 to everyone, although the route is public: an <img> tag cannot send
// a sign-in header, so an artwork request must be answered by the route itself.
// A key the cache does not hold is "not found", never "sign in".

const unknownKey = "a".repeat(64);

test("the artwork address answers a request with no sign-in, as an image tag sends it (#1146)", async ({ page, playwright, baseURL }) => {
  const anonymous = await playwright.request.newContext({ baseURL });
  try {
    const response = await anonymous.get(`/api/metadata/artwork/${unknownKey}`);
    expect(response.status(), "the route answered 401 although it is public").not.toBe(401);
    expect(response.status()).not.toBe(403);
    // And a path that is not public still asks for a sign-in, so the answer above is the route's own.
    expect((await anonymous.get("/api/movies/page")).status()).toBe(401);
  } finally {
    await anonymous.dispose();
  }

  // The signed-in browser asking for it the way a poster does, with no header: the page, not a script, makes the request.
  await authenticateAndNavigate(page, "/movies");
  const asked = await page.evaluate(async (key) => (await fetch(`/api/metadata/artwork/${key}`, { credentials: "omit" })).status, unknownKey);
  expect(asked).not.toBe(401);
});

import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * The resolution flow for a title the metadata provider has stopped listing.
 *
 * #357 asks for phone, keyboard and screen-reader evidence of this flow, and
 * the condition cannot be seeded: it only exists once a real provider lookup
 * comes back missing. So this stands up a broker that answers 404 for every
 * lookup, points Deluno at it, and drives the notice that results.
 *
 * It runs under both Playwright projects, so the phone half is the Pixel 7 run
 * of the same assertions rather than a separate, weaker test.
 */

async function authHeaders(page: import("@playwright/test").Page) {
  const token = await page.evaluate(() => sessionStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/** A broker that has never heard of anything. */
function startMissingRecordBroker(): Promise<{ url: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server: Server = createServer((_request, response) => {
      response.writeHead(404, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ status_code: 34, status_message: "The resource you requested could not be found." }));
    });
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address() as AddressInfo;
      resolve({
        url: `http://127.0.0.1:${port}`,
        close: () => new Promise<void>((done) => server.close(() => done()))
      });
    });
  });
}

test.describe("metadata recovery", () => {
  test("resolves a title the provider dropped, by keyboard and on a phone", async ({ page }) => {
    const broker = await startMissingRecordBroker();
    let headers: Record<string, string> = {};
    let movieId: string | undefined;
    let previousBrokerUrl: string | undefined;

    try {
      await authenticateAndNavigate(page, "/movies");
      headers = await authHeaders(page);

      const settingsBefore = await (await page.request.get("/api/settings/", { headers })).json() as {
        metadataBrokerUrl?: string;
      };
      previousBrokerUrl = settingsBefore.metadataBrokerUrl;

      await page.request.patch("/api/settings/", {
        headers,
        data: { metadataBrokerUrl: broker.url }
      });

      const title = `Provider dropout ${Date.now()}`;
      const create = await page.request.post("/api/movies/", {
        headers,
        data: { title, releaseYear: 2024, monitored: true, metadataProviderId: "999999901" }
      });
      expect(create.ok()).toBe(true);
      const movie = await create.json() as { id: string };
      movieId = movie.id;

      // The real refresh, against a broker that has nothing. This is what
      // produces the condition; nothing here writes the issue directly.
      await page.request.post(`/api/movies/${movie.id}/metadata/refresh`, { headers });

      const issue = await page.request.get(`/api/movies/${movie.id}/metadata/issue`, { headers });
      const issueBody = (await issue.text()).trim();
      // Asserted rather than skipped: a skip here would quietly delete this
      // whole coverage the day the condition stopped being produced, which is
      // exactly the day it would matter.
      expect(
        issueBody,
        "The broker fixture did not produce a provider-missing condition."
      ).toContain("provider-record-missing");

      await page.goto(`/movies/${movie.id}`);

      // Screen-reader shape: a named region, not an unlabelled block of text.
      const notice = page.getByRole("region", { name: /no longer listed by/i });
      await expect(notice).toBeVisible();
      await expect(notice).toContainText("kept the title, monitoring, history, and files");

      // Every choice is a named control. On a phone these wrap rather than
      // sitting in a row, which is exactly why this runs in both projects.
      const tryAgain = notice.getByRole("button", { name: "Try again" });
      const findAnother = notice.getByRole("button", { name: "Find another match" });
      const keep = notice.getByRole("button", { name: /^Keep this/ });
      for (const control of [tryAgain, findAnother, keep]) {
        await expect(control).toBeVisible();
        await expect(control).toBeEnabled();
      }

      // Keyboard: reach the whole flow, and confirm the notice does not trap
      // focus once the last choice has been passed.
      await tryAgain.focus();
      await expect(tryAgain).toBeFocused();
      await page.keyboard.press("Tab");
      await expect(findAnother).toBeFocused();
      await page.keyboard.press("Tab");
      await expect(keep).toBeFocused();

      // Resolving it clears the notice, and the title survives.
      await keep.press("Enter");
      await expect(notice).toBeHidden();

      const after = await page.request.get(`/api/movies/${movie.id}`, { headers });
      expect(after.ok()).toBe(true);
      expect((await after.json() as { title: string }).title).toBe(title);

      // And it stays gone while the evidence is unchanged.
      await page.reload();
      await expect(page.getByRole("region", { name: /no longer listed by/i })).toHaveCount(0);
    } finally {
      // This suite shares one database across every test, so a fixture left
      // behind is not this test's problem - it is the next test's failure, in
      // a file that never mentioned metadata. Clean up even when the
      // assertions above threw.
      if (movieId) {
        const removed = await page.request.post("/api/movies/bulk", {
          headers,
          data: { movieIds: [movieId], operation: "remove" }
        });
        expect(
          removed.ok(),
          "The fixture movie was not removed; the shared smoke database is now polluted."
        ).toBe(true);
        expect((await page.request.get(`/api/movies/${movieId}`, { headers })).status()).toBe(404);
      }

      if (previousBrokerUrl !== undefined) {
        await page.request.patch("/api/settings/", {
          headers,
          data: { metadataBrokerUrl: previousBrokerUrl }
        });
      }

      await broker.close();
    }
  });
});

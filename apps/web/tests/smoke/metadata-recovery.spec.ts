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

/**
 * A broker that answers every lookup with one status code.
 *
 * 404 is the provider saying the record is gone. 503 is the provider saying
 * nothing at all - and the whole risk in this area is that the second gets
 * read as the first, which would tell an owner their title had been removed
 * from TMDb every time TMDb had a bad afternoon.
 */
function startBrokerAnswering(status: 404 | 503): Promise<{ url: string; close: () => Promise<void> }> {
  const body = status === 404
    ? { status_code: 34, status_message: "The resource you requested could not be found." }
    : { status_code: 43, status_message: "The service is temporarily unavailable. Try again later." };
  return new Promise((resolve) => {
    const server: Server = createServer((_request, response) => {
      response.writeHead(status, { "Content-Type": "application/json" });
      response.end(JSON.stringify(body));
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
    const broker = await startBrokerAnswering(404);
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

  test("reads a provider outage as an outage, not as a deletion", async ({ page }) => {
    // The same fixture, one status code different. #357 asks that a transient
    // provider failure follow retry/backoff rather than be misclassified as a
    // deletion, and the failure mode is silent: nothing is thrown, nothing
    // looks broken, the owner is simply told the wrong thing about their
    // library. So this asserts on the absence - no issue, no notice, nothing
    // to acknowledge.
    const broker = await startBrokerAnswering(503);
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

      const title = `Provider outage ${Date.now()}`;
      const create = await page.request.post("/api/movies/", {
        headers,
        data: { title, releaseYear: 2024, monitored: true, metadataProviderId: "999999902" }
      });
      expect(create.ok()).toBe(true);
      const movie = await create.json() as { id: string };
      movieId = movie.id;

      // 503 is "come back later", so the refresh reports the provider as
      // unavailable. 409 here would mean Deluno had decided the record was
      // deleted, which is the defect this test exists to catch.
      const refresh = await page.request.post(`/api/movies/${movie.id}/metadata/refresh`, { headers });
      expect(
        refresh.status(),
        "A provider outage was reported as something other than an outage."
      ).toBe(503);

      // Repeat it. Backoff is only worth anything if the classification is
      // stable across attempts; a second call that finally called it a
      // deletion would be the same defect arriving one refresh late.
      const refreshAgain = await page.request.post(`/api/movies/${movie.id}/metadata/refresh`, { headers });
      expect(refreshAgain.status()).toBe(503);

      // No stored condition, so nothing for the owner to dismiss and nothing
      // to un-dismiss when the provider comes back.
      const issue = await page.request.get(`/api/movies/${movie.id}/metadata/issue`, { headers });
      expect(issue.ok()).toBe(true);
      const issueBody = (await issue.text()).trim();
      expect(
        issueBody === "" || issueBody === "null",
        `An outage recorded a title-scoped provider issue: ${issueBody}`
      ).toBe(true);

      // #338: the 503 carries the typed failure rather than an empty body.
      // Deluno knows which provider it asked and what happened; throwing that
      // away at the boundary is what left every surface saying "could not be
      // refreshed" and nothing else.
      const outage = await refreshAgain.json() as {
        code?: string;
        message?: string;
        failure?: {
          serviceType?: string;
          serviceName?: string;
          operation?: string;
          kind?: string;
          retryState?: string;
          nextAction?: string;
          summary?: string;
        } | null;
      };
      expect(outage.code).toBe("metadata-provider-unavailable");
      expect(outage.message ?? "").not.toHaveLength(0);
      expect(outage.failure?.serviceType).toBe("metadata");
      expect(outage.failure?.kind).toBe("Unavailable");
      expect(outage.failure?.retryState).toBe("RetryScheduled");
      expect(outage.failure?.nextAction ?? "").not.toHaveLength(0);

      // The failure has to name a service, not the Deluno function that
      // asked. "metadata.broker.resolve metadata.broker.resolve failed:
      // metadata.broker.resolve returned transient HTTP 503." was the real
      // summary until this line existed.
      expect(outage.failure?.serviceName).not.toBe(outage.failure?.operation);
      expect(outage.failure?.summary ?? "").not.toContain("metadata.broker.resolve returned");

      // And the title page stays ordinary: no calm notice, because there is
      // nothing calm to say. Refreshing from the page says what happened
      // instead of one fixed sentence that fits every possible cause.
      await page.goto(`/movies/${movie.id}`);
      await expect(page.getByRole("heading", { name: title })).toBeVisible();
      await expect(page.getByRole("region", { name: /no longer listed by/i })).toHaveCount(0);

      await page.getByRole("button", { name: "Refresh metadata" }).click();
      const toast = page.getByText(/could not answer|temporarily unavailable/i).first();
      await expect(toast).toBeVisible();
      await expect(page.getByText("This movie's metadata could not be refreshed.")).toHaveCount(0);
    } finally {
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

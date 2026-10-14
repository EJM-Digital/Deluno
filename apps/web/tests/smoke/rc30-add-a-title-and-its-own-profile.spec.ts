import { mkdirSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// rc.30 live test, #1145 and #1157. A person adds a film and chooses its quality
// profile in the Add window, and "search for it as soon as it is added" does what
// it says; later they give a film its own profile in Edit and it stays that film's
// profile. The page, the dialog and the drawer are driven with real clicks; the
// library and the two profiles are made through the API only to set the stage.

async function authHeaders(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function makeProfile(page: Page, headers: Record<string, string>, name: string) {
  const model = await page.request.get("/api/quality-model", { headers });
  const tier = (await model.json() as { tiers: Array<{ name: string }> }).tiers[0]?.name;
  const create = await page.request.post("/api/quality-profiles", {
    headers,
    data: { name, mediaType: "movies", cutoffQuality: tier, allowedQualities: tier, customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false }
  });
  expect(create.ok(), `POST /api/quality-profiles failed: ${create.status()}`).toBe(true);
  return await create.json() as { id: string; name: string };
}

test("adding a film lets you choose its profile, and an unticked search box means no search (#1145)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The Add window is the same on a phone; the desktop run proves the behaviour.");
  test.setTimeout(90_000);
  await authenticateAndNavigate(page, "/movies");
  const headers = await authHeaders(page);
  const stamp = Date.now();
  const root = path.join(os.tmpdir(), "deluno-playwright-libraries", `add-${stamp}`);
  mkdirSync(root, { recursive: true });
  const libraryProfile = await makeProfile(page, headers, `Add-Library-Profile-${stamp}`);
  const chosenProfile = await makeProfile(page, headers, `Add-Chosen-Profile-${stamp}`);
  const libraryResponse = await page.request.post("/api/libraries", {
    headers,
    data: {
      name: `Add-Library-${stamp}`, mediaType: "movies", purpose: "collection", rootPath: root, qualityProfileId: libraryProfile.id,
      autoSearchEnabled: true, missingSearchEnabled: true, upgradeSearchEnabled: true
    }
  });
  expect(libraryResponse.ok(), `POST /api/libraries failed: ${libraryResponse.status()}`).toBe(true);
  const library = await libraryResponse.json() as { id: string; name: string };
  const created: string[] = [];

  try {
    const searches: string[] = [];
    page.on("request", (request) => {
      if (request.method() === "POST" && /\/api\/movies\/[^/]+\/search/.test(request.url())) searches.push(request.url());
    });

    async function addManually(title: string, searchNow: boolean) {
      await page.goto("/movies?add=true");
      const dialog = page.getByRole("dialog", { name: "Add movie" });
      await expect(dialog).toBeVisible();
      await dialog.getByRole("textbox", { name: "What do you want to add?" }).fill(title);
      // The library and the profile are chosen here, before anything is searched.
      await dialog.getByLabel("Library").selectOption({ label: library.name });
      await dialog.getByLabel("Quality profile").selectOption({ label: chosenProfile.name });
      const searchBox = dialog.getByRole("checkbox", { name: /Search for it as soon as it is added/ });
      if ((await searchBox.isChecked()) !== searchNow) await searchBox.click();
      expect(await searchBox.isChecked()).toBe(searchNow);
      const posted = page.waitForResponse((response) => response.request().method() === "POST" && ["/api/movies", "/api/movies/"].includes(new URL(response.url()).pathname));
      await dialog.getByRole("button", { name: "Add movie manually" }).click();
      const response = await posted;
      expect(response.ok(), `POST /api/movies/ failed: ${response.status()}`).toBe(true);
      // The page moves on once the film is added, so the response body is gone; find the film by its title.
      const found = await (await page.request.get(`/api/movies/page?search=${encodeURIComponent(title)}`, { headers })).json() as { items: Array<{ id: string; title: string }> };
      const movie = found.items.find((item) => item.title === title);
      expect(movie, `${title} was not added`).toBeTruthy();
      created.push(movie!.id);
      return { ...movie!, asked: response.request().postDataJSON() as { searchForMissingNow?: boolean } };
    }

    // Unticked: the film is added, judged by the profile that was picked, and nothing searches for it.
    const quiet = await addManually(`Quiet Add ${stamp}`, false);
    const quietRow = await (await page.request.get(`/api/movies/${quiet.id}`, { headers })).json() as { qualityProfileId?: string | null; libraryId?: string | null };
    expect(quiet.asked.searchForMissingNow, "the unticked box was sent as a search").toBe(false);
    expect(quietRow.qualityProfileId).toBe(chosenProfile.id);
    expect(quietRow.libraryId).toBe(library.id);
    await page.waitForTimeout(2_500); // reason: shows that no search request follows; absence can only be shown by giving a wrong request real time to appear
    expect(searches, "a search request was sent although the box was unticked").toHaveLength(0);
    const history = await (await page.request.get("/api/movies/search-history", { headers })).json();
    expect(JSON.stringify(history)).not.toContain(quiet.id);

    // Ticked: the same window asks for a search when the box is ticked.
    const loud = await addManually(`Loud Add ${stamp}`, true);
    expect(loud.asked.searchForMissingNow, "the ticked box was not sent as a search").toBe(true);
  } finally {
    for (const id of created) await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [id], operation: "remove" }, timeout: 10_000 }).catch(() => undefined);
    await page.request.delete(`/api/libraries/${library.id}`, { headers, timeout: 10_000 }).catch(() => undefined);
    await page.request.delete(`/api/quality-profiles/${chosenProfile.id}`, { headers, timeout: 10_000 }).catch(() => undefined);
    await page.request.delete(`/api/quality-profiles/${libraryProfile.id}`, { headers, timeout: 10_000 }).catch(() => undefined);
  }
});

test("a film given its own profile in Edit keeps it, and can go back to following its library (#1157)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The Edit drawer is the same on a phone; the desktop run proves the behaviour.");
  test.setTimeout(90_000);
  await authenticateAndNavigate(page, "/movies");
  const headers = await authHeaders(page);
  const stamp = Date.now();
  const own = await makeProfile(page, headers, `Own-Profile-${stamp}`);
  const create = await page.request.post("/api/movies/", { headers, data: { title: `Own Profile Film ${stamp}`, releaseYear: 1999, monitored: false } });
  expect(create.ok()).toBe(true);
  const movie = await create.json() as { id: string };

  try {
    await page.goto(`/movies/${movie.id}`);
    await page.getByRole("button", { name: "Edit movie" }).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Quality profile").selectOption({ label: own.name });
    const saved = page.waitForResponse((response) => response.request().method() === "POST" && new URL(response.url()).pathname === "/api/movies/bulk/quality-profile");
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    expect((await saved).ok()).toBe(true);

    // It is the film's own profile now, on the server and in the drawer after a reload.
    const row = await (await page.request.get(`/api/movies/${movie.id}`, { headers })).json() as { qualityProfileId?: string | null };
    expect(row.qualityProfileId).toBe(own.id);
    await page.reload();
    await page.getByRole("button", { name: "Edit movie" }).click();
    await expect(page.getByRole("dialog").getByLabel("Quality profile")).toHaveValue(own.id);

    // And it can follow its library's profile again.
    await page.getByRole("dialog").getByLabel("Quality profile").selectOption({ label: "Library default" });
    const back = page.waitForResponse((response) => response.request().method() === "POST" && new URL(response.url()).pathname === "/api/movies/bulk/quality-profile");
    await page.getByRole("dialog").getByRole("button", { name: "Save", exact: true }).click();
    expect((await back).ok()).toBe(true);
    const followed = await (await page.request.get(`/api/movies/${movie.id}`, { headers })).json() as { qualityProfileId?: string | null };
    expect(followed.qualityProfileId ?? "").not.toBe(own.id);
  } finally {
    await page.request.post("/api/movies/bulk", { headers, data: { movieIds: [movie.id], operation: "remove" } });
    await page.request.delete(`/api/quality-profiles/${own.id}`, { headers });
  }
});

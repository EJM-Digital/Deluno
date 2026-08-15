import { expect, request as playwrightRequest, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("dashboard workflow", () => {
  test("starts in the dashboard and opens direct add flows", async ({ page }) => {
    await authenticateAndNavigate(page, "/");

    await expect(page.getByRole("heading", { name: "Dashboard", exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: "Add a movie" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Add a show" })).toBeVisible();

    await page.getByRole("link", { name: "Add a movie" }).click();

    await expect(page).toHaveURL(/\/movies\?add=true/);
    await expect(page.getByRole("dialog", { name: "Add movie" })).toBeVisible();
    await expect(page.getByRole("textbox", { name: "What do you want to add?" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Search now" })).toBeVisible();
    await expect(page.getByText("Can’t find it? Add it manually", { exact: true })).toBeVisible();
  });

  test("shows real empty-state information instead of invented dashboard activity", async ({ page }) => {
    await authenticateAndNavigate(page, "/");

    await expect(page.getByText("No search sources or download clients", { exact: true })).toBeVisible();
    await expect(page.getByText("No downloads, processing, or imports need your attention right now.", { exact: true })).toBeVisible();
    await expect(page.getByText("Library and health over time", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Download speed", { exact: true })).toHaveCount(0);
  });

  test("makes library display, order, and refine controls readable without hiding advanced choices", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");

    await page.getByRole("button", { name: /^Display/ }).click();
    await expect(page.getByRole("heading", { name: "Choose how your library feels" })).toBeVisible();
    await expect(page.getByRole("button", { name: /^Selected Poster grid/ })).toBeVisible();
    await expect(page.getByRole("button", { name: /^Compact list/ })).toBeVisible();
    await expect(page.getByText("What each poster shows", { exact: true })).toBeVisible();

    await page.getByRole("button", { name: /^Order/ }).click();
    await expect(page.getByRole("heading", { name: "Put the right titles first" })).toBeVisible();
    await expect(page.getByText("More ways to order your library", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Ascending" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Descending" })).toBeVisible();

    await page.getByRole("button", { name: /^Refine/ }).click();
    await expect(page.getByRole("heading", { name: "Narrow the library without losing your place" })).toBeVisible();
    await expect(page.getByText("Precise rules", { exact: true })).toBeVisible();
    await expect(page.getByText("Saved library views", { exact: true })).toBeVisible();
  });

  test("uses lifecycle status, not monitoring, for movie and TV poster markers", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");
    const token = await page.evaluate(() => sessionStorage.getItem("deluno-auth-token"));
    const headers = token ? { Authorization: `Bearer ${token}` } : {};
    const api = await playwrightRequest.newContext({
      baseURL: process.env.DELUNO_API_ORIGIN ?? "http://127.0.0.1:5199",
      extraHTTPHeaders: headers
    });
    const scenarios = [
      {
        route: "/movies",
        placeholder: "Search movies…",
        createPath: "/api/movies/",
        removePath: "/api/movies/bulk",
        createData: (title: string, monitored: boolean) => ({ title, releaseYear: 2024, monitored }),
        removeData: (id: string) => ({ movieIds: [id], operation: "remove" })
      },
      {
        route: "/tv",
        placeholder: "Search TV shows…",
        createPath: "/api/series/",
        removePath: "/api/series/bulk",
        createData: (title: string, monitored: boolean) => ({ title, startYear: 2024, monitored }),
        removeData: (id: string) => ({ seriesIds: [id], operation: "remove" })
      }
    ];

    for (const scenario of scenarios) {
      for (const monitored of [true, false]) {
        const title = `${monitored ? "Monitored" : "Passive"} presentation ${scenario.route} ${Date.now()}`;
        const monitoringLabel = monitored ? "Monitored" : "Not monitored";
        let created: { id: string } | null = null;
        try {
          const create = await api.post(scenario.createPath, {
            data: scenario.createData(title, monitored)
          });
          expect(create.ok()).toBe(true);
          created = await create.json() as { id: string };

          await page.goto(scenario.route);
          await page.getByPlaceholder(scenario.placeholder).fill(title);
          const titleCard = page.getByRole("button", { name: new RegExp(title) }).last();
          await expect(titleCard).toBeVisible();

          await page.getByRole("button", { name: /^Display/ }).click();
          await page.getByRole("button", { name: /Medium Balanced/ }).click();
          const mediumMarker = page.getByRole("img", { name: "Missing" });
          await expect(mediumMarker).toBeVisible();
          await expect(mediumMarker.locator("span").first()).toHaveClass(/bg-warning/);
          await expect(titleCard.getByText(monitoringLabel, { exact: true })).toBeVisible();

          await page.getByRole("button", { name: /Small More titles/ }).click();
          const smallMarker = page.getByRole("img", { name: "Missing" });
          await expect(smallMarker).toBeVisible();
          await expect(smallMarker).toHaveClass(/bg-warning/);
          await expect(smallMarker).not.toHaveClass(/bg-success/);
        } finally {
          if (created) {
            await api.post(scenario.removePath, {
              data: scenario.removeData(created.id)
            });
          }
        }
      }
    }

    await api.dispose();
  });

  test("keeps Library setup as an expandable sidebar hierarchy", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings");

    await expect(page.getByRole("heading", { name: "Library setup" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Start with your library" })).toBeVisible();
    if (testInfo.project.name === "mobile") {
      await page.getByRole("button", { name: "More destinations" }).click();
      await expect(page.getByLabel("Panel").getByRole("link", { name: "Library setup", exact: true })).toBeVisible();
      return;
    }

    const tree = page.locator("aside").getByRole("navigation", { name: "Library setup" });
    await expect(tree.getByRole("button", { name: "Collapse Library setup" })).toHaveAttribute("aria-expanded", "true");
    for (const destination of ["/settings/media-management", "/indexers", "/settings/policy-sets", "/settings/lists"]) {
      await expect(tree.locator(`a[href="${destination}"]`).first()).toHaveCount(1);
    }
    await expect(page.getByRole("heading", { name: "Setup status" })).toBeVisible();
    await expect(page.getByText("Other configuration", { exact: true })).toHaveCount(0);
  });

  test("uses collapsible owner submenus instead of an all-settings wall", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The same hierarchy is covered in the mobile drawer test.");
    await authenticateAndNavigate(page, "/settings");

    await expect(page.getByRole("heading", { name: "All configuration" })).toHaveCount(0);
    const tree = page.locator("aside").getByRole("navigation", { name: "Library setup" });
    await page.goto("/settings/media-management");
    for (const destination of ["/settings/processing", "/settings/destination-rules", "/settings/metadata", "/settings/tags"]) {
      await expect(tree.locator(`a[href="${destination}"]`)).toHaveCount(1);
    }

    await page.goto("/settings/policy-sets");
    for (const destination of ["/settings/profiles", "/settings/quality", "/settings/custom-formats"]) {
      await expect(tree.locator(`a[href="${destination}"]`)).toHaveCount(1);
    }

    await page.goto("/settings/lists");
    await expect(tree.locator('a[href="/settings/lists"]').first()).toHaveCount(1);

    await page.goto("/settings/general");
    const maintenance = page.locator("aside").getByRole("navigation", { name: "System controls" });
    for (const destination of ["/settings/migration", "/settings/notifications", "/settings/ui", "/setup-guide", "/system/backups", "/system/updates", "/system/api", "/system/docs"]) {
      await expect(maintenance.locator(`a[href="${destination}"]`)).toHaveCount(1);
    }

    await page.goto("/indexers/indexers");
    for (const destination of ["/indexers/indexers", "/indexers/download-clients", "/indexers/library-routing"]) {
      await expect(tree.locator(`a[href="${destination}"]`)).toHaveCount(1);
    }
  });

  test("keeps the same configuration tree in every configuration family", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The mobile drawer has the same destination tree.");
    await authenticateAndNavigate(page, "/settings/media-management");

    const expectedSections = ["/settings/media-management", "/indexers", "/settings/policy-sets", "/settings/lists"];
    const navigator = page.locator("aside").getByRole("navigation", { name: "Library setup" });
    await expect(navigator).toBeVisible();
    for (const destination of expectedSections) {
      await expect(navigator.locator(`a[href="${destination}"]`)).toHaveCount(1);
    }

    await page.goto("/indexers");
    await expect(navigator.getByRole("link", { name: "Connections", exact: true })).toBeVisible();
    await page.goto("/search-cycles");
    await expect(page.getByRole("navigation", { name: "Automation and transfer status" }).getByRole("link", { name: "Automation", exact: true })).toBeVisible();
    await page.goto("/system");
    await expect(page.locator("aside").getByRole("navigation", { name: "System controls" }).getByRole("link", { name: "System & settings", exact: true })).toBeVisible();
  });

  test("keeps installation-wide settings under Maintain Deluno", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The desktop sidebar owns this hierarchy test.");
    await authenticateAndNavigate(page, "/settings/general");

    const maintenance = page.locator("aside").getByRole("navigation", { name: "System controls" });
    await expect(maintenance.getByRole("button", { name: "Collapse System & settings" })).toHaveAttribute("aria-expanded", "true");
    await expect(maintenance.getByRole("link", { name: "General", exact: true })).toBeVisible();
    await expect(page.locator("aside").getByRole("navigation", { name: "Library setup" }).getByRole("link", { name: "System & settings", exact: true })).toHaveCount(0);
  });

  test("lets the Library setup sidebar tree expand and collapse independently", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The mobile hierarchy lives in the navigation drawer.");
    await authenticateAndNavigate(page, "/settings/media-management");

    const setupToggle = page.getByRole("button", { name: "Collapse Library setup" });
    const sidebar = page.locator("aside");
    await expect(setupToggle).toHaveAttribute("aria-expanded", "true");
    await setupToggle.click();
    await expect(page.getByRole("button", { name: "Expand Library setup" })).toHaveAttribute("aria-expanded", "false");
    await expect(sidebar.getByRole("link", { name: "Library details", exact: true })).toHaveCount(0);

    await page.getByRole("button", { name: "Expand Library setup" }).click();
    const libraryToggle = page.getByRole("button", { name: "Collapse Library", exact: true });
    await expect(libraryToggle).toHaveAttribute("aria-expanded", "true");
    await libraryToggle.click();
    await expect(page.getByRole("button", { name: "Expand Library", exact: true })).toHaveAttribute("aria-expanded", "false");
  });

  test("keeps every system maintenance destination visible from System", async ({ page }) => {
    await authenticateAndNavigate(page, "/system");

    const systemNavigation = page.getByRole("navigation").filter({ has: page.getByRole("link", { name: "Health", exact: true }) });
    for (const destination of ["/system", "/system/audit", "/system/api", "/system/docs", "/system/backups", "/system/updates"]) {
      await expect(systemNavigation.locator(`a[href="${destination}"]`)).toHaveCount(1);
    }
  });

  test("opens a setup category with the keyboard", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The mobile drawer is touch-first.");
    await authenticateAndNavigate(page, "/settings");

    const sources = page.locator("aside").getByRole("navigation", { name: "Library setup" }).getByRole("link", { name: "Connections", exact: true });
    await sources.focus();
    await page.keyboard.press("Enter");

    await expect(page).toHaveURL(/\/indexers/);
    await expect(page.getByRole("heading", { name: "Connect Deluno" })).toBeVisible();
  });

  test("keeps failed-download handling together with automation and recovery", async ({ page }) => {
    await authenticateAndNavigate(page, "/search-cycles");

    await expect(page.getByRole("heading", { name: "Failed download handling" })).toBeVisible();
    await expect(page.getByLabel("Act after this many strikes")).toHaveValue("3");
    await expect(page.getByText("Block this release", { exact: true })).toBeVisible();
    await expect(page.getByText("Search for a replacement", { exact: true })).toBeVisible();
    await expect(page.getByText("Remove client entry", { exact: true })).toBeVisible();
    await expect(page.getByText("Purge residual files", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Save failed-download handling" })).toBeVisible();
  });

  test("keeps navigation compact and explains each destination after it opens", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings");

    if (testInfo.project.name === "mobile") {
      await page.getByRole("button", { name: "More destinations" }).click();
      const drawer = page.getByLabel("Panel");
      await expect(drawer.getByText("What Deluno is doing", { exact: true })).toBeVisible();
      await expect(drawer.getByText("Set up your library", { exact: true })).toBeVisible();
      await expect(drawer.getByText("Maintain Deluno", { exact: true })).toBeVisible();
      await expect(drawer.getByText("Control room", { exact: true })).toHaveCount(0);
      await drawer.getByRole("link", { name: "Automation", exact: true }).click();
      await expect(page.getByText("Choose what Deluno should search for, retry, and upgrade next", { exact: true })).toBeVisible();
      return;
    }

    await expect(page.getByLabel("Automation and transfer status")).toBeVisible();
    await expect(page.getByLabel("System controls")).toBeVisible();
    await expect(page.getByText("Browse, add, and plan the movies and shows you care about.", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Follow the automatic path from search to download, processing, import, and the record of every decision.", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Change the folders, sources, download clients, plans, and rules Deluno uses.", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Check health, keep backups, install updates, and manage advanced access.", { exact: true })).toHaveCount(0);
    await expect(page.getByText("How Deluno manages your media", { exact: true })).toBeVisible();
    await page.getByRole("link", { name: "Transfers" }).click();
    await expect(page.getByText("Follow downloads through processing and safe import", { exact: true })).toBeVisible();
    await expect(page.getByText("Control room", { exact: true })).toHaveCount(0);
  });

  test("explains familiar media automation terms without hiding Deluno's model", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings");

    await page.getByTitle("Open glossary").click();
    await expect(page.getByRole("heading", { name: "Glossary" })).toBeVisible();
    await expect(page.getByText("Media Plan", { exact: true })).toBeVisible();
    await expect(page.getByText("Search Source", { exact: true })).toBeVisible();
    await expect(page.getByText("Download Health & Cleanup", { exact: true })).toBeVisible();
    await expect(page.getByText("Guide-backed Plan", { exact: true })).toBeVisible();
  });

  test("starts a media plan from an understandable scenario", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/policy-sets");

    await expect(page.getByRole("heading", { name: "Start with the library you want" })).toBeVisible();
    await page.getByRole("button", { name: /Family movies/i }).click();

    await expect(page.locator('input[value="Family Movies 1080p"]')).toBeVisible();
    await expect(page.getByRole("button", { name: "Create media plan" })).toBeVisible();
  });

  test("makes import lists a visible, understandable discovery option", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings");

    if (testInfo.project.name === "mobile") await page.getByRole("button", { name: "More destinations" }).click();
    const navigation = testInfo.project.name === "mobile"
      ? page.getByLabel("Panel")
      : page.locator("aside").getByRole("navigation", { name: "Library setup" });
    const importLists = navigation.getByRole("link", { name: "Discover media", exact: true });
    await expect(importLists).toHaveAttribute("href", "/settings/lists");
    await importLists.click();

    await expect(page.getByRole("heading", { name: "Import lists", exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Add an import list" })).toBeVisible();
    await expect(page.getByText(/Paste a public list URL/)).toBeVisible();
  });

  test("uses a custom list URL for public MDbList lists without a separate provider", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/lists");

    const listType = page.getByRole("combobox").first();
    await expect(listType).toHaveValue("url-list");
    await expect(listType.locator('option[value="mdblist"]')).toHaveCount(0);
    await expect(page.getByText(/Paste a public list URL/)).toBeVisible();
    await expect(page.getByText("MDbList access token", { exact: true })).toHaveCount(0);
    await expect(page.getByPlaceholder(/Paste MDbList access token/i)).toHaveCount(0);
  });

  test("keeps library details discoverable and provider credentials out of normal setup", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings/media-management");

    if (testInfo.project.name === "mobile") await page.getByRole("button", { name: "More destinations" }).click();
    const navigation = testInfo.project.name === "mobile"
      ? page.getByLabel("Panel")
      : page.locator("aside").getByRole("navigation", { name: "Library setup" });
    const libraryDetails = navigation.getByRole("link", { name: "Metadata & sidecars", exact: true });
    await expect(libraryDetails).toHaveAttribute("href", "/settings/metadata");
    await libraryDetails.click();

    await expect(page.getByRole("heading", { name: "Metadata & sidecars", exact: true })).toBeVisible();
    await expect(page.getByText("What Deluno saves", { exact: true })).toBeVisible();
    await expect(page.getByText(/There are no provider keys to set here/)).toBeVisible();
    await expect(page.getByText("TMDb API key", { exact: true })).toHaveCount(0);
    await expect(page.getByText("OMDb API key", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Provider route", { exact: true })).toHaveCount(0);
  });

  test("keeps processor callbacks optional for the watched-output workflow", async ({ page }) => {
    await page.route((url) => url.pathname === "/api/integrations/processors/connections", async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ contentType: "application/json", body: "[]" });
        return;
      }
      expect(route.request().postDataJSON()).toMatchObject({
        name: "Processed media notifier",
        provider: "generic-webhook",
        submissionUrl: "https://processor.example.test/webhooks/deluno"
      });
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ id: "processor-1", name: "Processed media notifier", provider: "generic-webhook", submissionUrl: "https://processor.example.test/webhooks/deluno", authHeaderName: "Authorization", secretConfigured: true, isEnabled: true, healthStatus: "unknown", lastHealthMessage: null, lastHealthTestUtc: null, createdUtc: "2026-08-14T00:00:00Z", updatedUtc: "2026-08-14T00:00:00Z" }) });
    });

    await authenticateAndNavigate(page, "/settings/processing");
    await expect(page.getByText("Optional processor notifications", { exact: true })).toBeVisible();
    await expect(page.getByText(/Most processed libraries only need a processed-files folder/)).toBeVisible();
    await page.getByPlaceholder("Processed media notifier").fill("Processed media notifier");
    await page.getByPlaceholder("https://processor.example/webhooks/deluno").fill("https://processor.example.test/webhooks/deluno");
    await page.getByRole("button", { name: "Save optional callback" }).click();

    await expect(page.getByText(/Optional completion callback saved/i)).toBeVisible();
  });

  test("makes external client queue removal an explicit manual setting", async ({ page }) => {
    await authenticateAndNavigate(page, "/indexers/download-clients");

    const setting = page.getByRole("switch", { name: "Allow removing client queue entries" });
    await expect(setting).toHaveAttribute("aria-checked", "false");
    await expect(page.getByText(/Allow a confirmed Remove action for items in external apps/)).toBeVisible();
    await expect(page.getByText(/deliberate manual action only/)).toBeVisible();
  });

  test("previews an import list without adding or searching titles", async ({ page }) => {
    await page.route((url) => /^\/api\/intake-sources\/[^/]+\/preview$/.test(url.pathname), async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({
          sourceId: "list-1", sourceName: "Weekend movies", provider: "mdblist", mediaType: "movies", targetLibraryName: null,
          fetchedCount: 2, shownCount: 2, isTruncated: false, warnings: ["No compatible target library is configured. Sync will not add any titles until you choose one."],
          items: [
            { title: "Arrival", year: 2016, mediaType: "movies", imdbId: "tt2543164", action: "would add", reason: "This title passes the list's available filters and would be added on sync.", matchConfidence: "high" },
            { title: "Dune", year: 2021, mediaType: "movies", imdbId: "tt1160419", action: "already in library", reason: "A matching title is already in this Deluno library.", matchConfidence: "high" }
          ]
        })
      });
    });

    await authenticateAndNavigate(page, "/settings/lists");
    await page.getByRole("textbox").nth(0).fill("Weekend movies");
    await page.getByRole("combobox").nth(0).selectOption("url-list");
    await page.getByRole("textbox").nth(1).fill("https://example.com/weekend-movies.txt");
    await page.getByRole("button", { name: "Add import list" }).click();
    await expect(page.getByText("Weekend movies", { exact: true })).toBeVisible();
    await page.getByTitle("Preview without adding titles").last().click();

    await expect(page.getByText("Preview ready. Nothing was added or searched.")).toBeVisible();
    await expect(page.getByText("Read-only preview", { exact: true })).toBeVisible();
    await expect(page.getByText("Arrival (2016)", { exact: true })).toBeVisible();
    await expect(page.getByText("Dune (2021)", { exact: true })).toBeVisible();
    await expect(page.getByText(/No compatible target library is configured/)).toBeVisible();
  });

  test("lets a user selectively approve previewed titles without running a full sync", async ({ page }) => {
    await page.route((url) => /^\/api\/intake-sources\/[^/]+\/preview$/.test(url.pathname), async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({
          sourceId: "list-select", sourceName: "Approved movies", provider: "url-list", mediaType: "movies", targetLibraryName: "Movies",
          fetchedCount: 2, shownCount: 2, isTruncated: false, warnings: [],
          items: [
            { title: "Arrival", year: 2016, mediaType: "movies", imdbId: "tt2543164", action: "would add", reason: "Eligible.", matchConfidence: "high" },
            { title: "Dune", year: 2021, mediaType: "movies", imdbId: "tt1160419", action: "would add", reason: "Eligible.", matchConfidence: "high" }
          ]
        })
      });
    });
    await page.route((url) => /^\/api\/intake-sources\/[^/]+\/approve-preview$/.test(url.pathname), async (route) => {
      const body = route.request().postDataJSON();
      expect(body).toEqual({ entries: [{ title: "Arrival", year: 2016, imdbId: "tt2543164" }], searchAfterAdd: false });
      await route.fulfill({ contentType: "application/json", body: JSON.stringify({ selectedCount: 1, matchedCount: 1, addedCount: 1, duplicateCount: 0, skippedCount: 0, errorCount: 0, searchRequested: false, summary: "Added Arrival." }) });
    });

    await authenticateAndNavigate(page, "/settings/lists");
    await page.getByRole("textbox").nth(0).fill("Approved movies");
    await page.getByRole("combobox").nth(0).selectOption("url-list");
    await page.getByRole("textbox").nth(1).fill("https://example.com/approved.txt");
    await page.getByRole("button", { name: "Add import list" }).click();
    await page.getByTitle("Preview without adding titles").last().click();

    await page.getByRole("checkbox", { name: "Dune (2021)" }).uncheck();
    await page.getByRole("button", { name: "Add selected", exact: true }).click();
    await expect(page.getByText(/1 title added from 1 approved preview entry/)).toBeVisible();
  });

  test("explains sources and downloads as connections", async ({ page }) => {
    await authenticateAndNavigate(page, "/indexers");

    await expect(page.getByRole("heading", { name: "Connect Deluno" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Manage indexers" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Manage clients" })).toBeVisible();
  });

  test("gives downloads and imports a clear next step", async ({ page }) => {
    await authenticateAndNavigate(page, "/queue");

    await expect(page.getByRole("heading", { name: "Your downloads, ready for your library." })).toBeVisible();
    await expect(page.getByRole("heading", { name: "What to do next" })).toBeVisible();
  });

  test("keeps download-health evidence available after queue work", async ({ page }) => {
    await authenticateAndNavigate(page, "/queue");

    await expect(page.getByRole("heading", { name: "Download health history" })).toBeVisible();
    await expect(page.getByText("Persisted import paths are redacted.")).toBeVisible();
  });

  test("keeps activity as a readable history", async ({ page }) => {
    await authenticateAndNavigate(page, "/activity");

    await expect(page.getByRole("heading", { name: /The record of what Deluno has done/ })).toBeVisible();
    await expect(page.getByText("Recent activity", { exact: true })).toBeVisible();
  });
});

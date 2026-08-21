import { expect, request as playwrightRequest, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("dashboard workflow", () => {
  test("starts in the dashboard and opens direct add flows", async ({ page }) => {
    await authenticateAndNavigate(page, "/");

    await expect(page.getByRole("heading", { name: "Dashboard", exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: "Add a movie" })).toBeVisible();

    await page.getByRole("link", { name: "Add a movie" }).click();

    await expect(page).toHaveURL(/\/movies\?add=true/);
    await expect(page.getByRole("dialog", { name: "Add movie" })).toBeVisible();
    await expect(page.getByRole("textbox", { name: "What do you want to add?" })).toBeVisible();
    await expect(page.getByText("Matches auto-refresh as you type, or press Enter to refresh now.", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Add movie manually" })).toBeVisible();
    await expect(page.getByText("Can’t find it? Add it manually", { exact: true })).toBeVisible();
  });

  test("shows real empty-state information instead of invented dashboard activity", async ({ page }) => {
    await authenticateAndNavigate(page, "/");

    // The dashboard states what is actually true of an empty install rather than
    // filling the space with invented activity. Setup guidance is offered, and
    // the library sections say plainly that they are empty.
    await expect(page.getByRole("heading", { name: "Build your media library" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Build my setup" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Recently added" })).toBeVisible();
    await expect(page.getByText("Nothing in the library yet", { exact: true })).toBeVisible();
  });

  test("makes library display, server-backed order, and refine controls readable", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");

    await page.getByRole("button", { name: /^Display/ }).click();
    await expect(page.getByRole("heading", { name: "Choose how your library feels" })).toBeVisible();
    await expect(page.getByRole("button", { name: /^Selected Poster grid/ })).toBeVisible();
    await expect(page.getByRole("button", { name: /^Compact list/ })).toBeVisible();
    await expect(page.getByText("What each poster shows", { exact: true })).toBeVisible();

    await page.getByRole("button", { name: /^Order/ }).click();
    await expect(page.getByRole("heading", { name: "Put the right titles first" })).toBeVisible();
    await expect(page.getByText("Every available order is performed by the paged catalogue query.", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Ascending" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Descending" })).toBeVisible();

    await page.getByRole("button", { name: /^Refine/ }).click();
    await expect(page.getByRole("heading", { name: "Narrow the library without losing your place" })).toBeVisible();
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
    await expect(page.getByRole("heading", { name: "Setup overview" })).toBeVisible();
    if (testInfo.project.name === "mobile") {
      await page.getByRole("button", { name: "More destinations" }).click();
      await expect(page.getByLabel("Panel").getByRole("link", { name: "Files & folders", exact: true })).toBeVisible();
      return;
    }

    // Every configuration area sets `tabsInToolbar`, so the sidebar shows one
    // row per area and the page's own toolbar is how you move between siblings.
    // See the comment on configurationNavAreas: the two must not do the same job
    // twice. Assert the area rows here, and the sibling tabs on the page below.
    const tree = page.locator("aside").getByRole("navigation", { name: "Library setup" });
    for (const destination of ["/settings/libraries", "/indexers/indexers", "/settings/policy-sets", "/settings/lists"]) {
      await expect(tree.locator(`a[href="${destination}"]`).first()).toHaveCount(1);
    }
    await expect(tree.getByRole("button", { name: /Collapse|Expand/ })).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Set up Deluno in order" })).toBeVisible();
    await expect(page.getByText("Other configuration", { exact: true })).toHaveCount(0);
  });

  test("keeps the same configuration tree in every configuration family", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The mobile drawer has the same destination tree.");
    await authenticateAndNavigate(page, "/settings/media-management");

    // Area rows, not their children — see the tabsInToolbar rule on
    // configurationNavAreas. Child pages live in the page toolbar.
    const expectedSections = ["/settings/libraries", "/indexers/indexers", "/settings/policy-sets", "/settings/lists"];
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
    await expect(page.locator("aside").getByRole("navigation", { name: "System controls" }).getByRole("link", { name: "System", exact: true })).toBeVisible();
  });

  test("keeps installation-wide settings under Maintain Deluno", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The desktop sidebar owns this hierarchy test.");
    await authenticateAndNavigate(page, "/settings/general");

    const maintenance = page.locator("aside").getByRole("navigation", { name: "System controls" });
    await expect(maintenance.getByRole("button", { name: /Collapse|Expand/ })).toHaveCount(0);
    await expect(maintenance.getByRole("link", { name: "System", exact: true })).toBeVisible();
    await expect(page.locator("aside").getByRole("navigation", { name: "Library setup" }).getByRole("link", { name: "Preferences", exact: true })).toHaveCount(0);
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
    await expect(page.getByRole("heading", { name: "Indexers", exact: true })).toBeVisible();
  });

  test("keeps failed-download handling together with automation and recovery", async ({ page }) => {
    await authenticateAndNavigate(page, "/search-cycles");

    await expect(page.getByRole("heading", { name: "Failed downloads" })).toBeVisible();
    await expect(page.getByLabel("Act after this many strikes")).toHaveValue("3");
    await expect(page.getByText("Block this release", { exact: true })).toBeVisible();
    await expect(page.getByText("Search for a replacement", { exact: true })).toBeVisible();
    await expect(page.getByText("Remove the client entry", { exact: true })).toBeVisible();
    await expect(page.getByText("Purge residual files", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Save failed-download handling" })).toBeVisible();
  });

  test("keeps navigation compact and explains each destination after it opens", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings");

    if (testInfo.project.name === "mobile") {
      await page.getByRole("button", { name: "More destinations" }).click();
      const drawer = page.getByLabel("Panel");
      await expect(drawer.getByRole("link", { name: "Automation", exact: true })).toBeVisible();
      await expect(drawer.getByRole("link", { name: "Files & folders", exact: true })).toBeVisible();
      await expect(drawer.getByRole("link", { name: "System", exact: true })).toBeVisible();
      await expect(drawer.getByText("Control room", { exact: true })).toHaveCount(0);
      await drawer.getByRole("link", { name: "Automation", exact: true }).click();
      await expect(page.getByText("What Deluno searches for on a schedule, and what it does when a download fails", { exact: true })).toBeVisible();
      return;
    }

    await expect(page.getByLabel("Automation and transfer status")).toBeVisible();
    await expect(page.getByLabel("System controls")).toBeVisible();
    await expect(page.getByText("Browse, add, and plan the movies and shows you care about.", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Follow the automatic path from search to download, processing, import, and the record of every decision.", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Change the folders, sources, download clients, plans, and rules Deluno uses.", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Check health, keep backups, install updates, and manage advanced access.", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Guided configuration for your media library, quality policy, automation, and runtime behaviour.").first()).toBeVisible();
    await page.getByRole("link", { name: "Transfers" }).click();
    await expect(page.getByText("Follow downloads through processing and safe import", { exact: true })).toBeVisible();
    await expect(page.getByText("Control room", { exact: true })).toHaveCount(0);
  });

  test("explains familiar media automation terms without hiding Deluno's model", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings");

    await page.getByTitle("Open glossary").click();
    const glossary = page.getByRole("dialog", { name: "Glossary" });
    await expect(glossary).toBeVisible();
    await expect(glossary.getByText("Media Plan", { exact: true })).toBeVisible();
    await expect(glossary.getByText("Search Source", { exact: true })).toBeVisible();
    await expect(glossary.getByText("Download Health & Cleanup", { exact: true })).toBeVisible();
    await expect(glossary.getByText("Guide-backed Plan", { exact: true })).toBeVisible();
  });

  test("starts a media plan from an understandable scenario", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/policy-sets");

    // List → drawer: the page is a list of plans; "New plan" opens the editor drawer.
    await expect(page.getByRole("heading", { name: "Media plans", exact: true }).first()).toBeVisible();
    await page.getByRole("button", { name: "New plan" }).first().click();
    await expect(page.getByRole("dialog", { name: "New media plan" })).toBeVisible();

    await page.getByLabel("Starter").selectOption("everyday-movies");
    await expect(page.getByLabel("Plan name")).toHaveValue("Default: Movies 1080p");
    await expect(page.getByRole("button", { name: "Create plan" })).toBeEnabled();

    // Leaving with edits asks first.
    await page.keyboard.press("Escape");
    await expect(page.getByRole("button", { name: "Discard" })).toBeVisible();
    await page.getByRole("button", { name: "Discard" }).click();
    await expect(page.getByRole("dialog", { name: "New media plan" })).toHaveCount(0);
  });

  test("makes import lists a visible, understandable discovery option", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings");

    if (testInfo.project.name === "mobile") await page.getByRole("button", { name: "More destinations" }).click();
    const navigation = testInfo.project.name === "mobile"
      ? page.getByLabel("Panel")
      : page.locator("aside").getByRole("navigation", { name: "Library setup" });
    const importLists = navigation.getByRole("link", { name: "Import lists", exact: true });
    await expect(importLists).toHaveAttribute("href", "/settings/lists");
    await importLists.click();

    await expect(page.getByRole("heading", { name: "Import lists", exact: true }).first()).toBeVisible();
    await page.getByRole("button", { name: "New list" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New import list" });
    await expect(drawer.getByText(/Paste a public list URL/)).toBeVisible();
    await expect(drawer.getByLabel("Check the list").locator('option[value="720"]')).toHaveText("Monthly");
  });

  test("uses a custom list URL for public MDbList lists without a separate provider", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/lists");
    await page.getByRole("button", { name: "New list" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New import list" });

    const listType = drawer.getByLabel("Provider");
    await expect(listType).toHaveValue("url-list");
    await expect(listType.locator('option[value="mdblist"]')).toHaveCount(0);
    await expect(drawer.getByText(/Paste a public list URL/)).toBeVisible();
    await expect(page.getByText("MDbList access token", { exact: true })).toHaveCount(0);
    await expect(page.getByPlaceholder(/Paste MDbList access token/i)).toHaveCount(0);
  });

  test("keeps library details discoverable and provider credentials out of normal setup", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings/media-management");

    // Metadata is a page inside the Files & folders area, so it is reached from
    // the page toolbar rather than the sidebar — see the tabsInToolbar rule.
    await page.getByRole("link", { name: "Metadata & sidecars", exact: true }).first().click();

    await expect(page.getByRole("heading", { name: "Metadata & sidecars", exact: true }).first()).toBeVisible();
    await expect(page.getByText("What Deluno saves", { exact: true })).toBeVisible();
    await expect(page.getByText(/there are no provider keys to set/)).toBeVisible();
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
    // Callbacks are optional: the empty state says so, and adding one is a drawer.
    await expect(page.getByRole("heading", { name: "Completion callbacks", exact: true })).toBeVisible();
    await expect(page.getByText(/Deluno watches the processed-files folder directly/)).toBeVisible();

    await page.getByRole("button", { name: "New callback" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New completion callback" });
    await drawer.getByLabel("Name", { exact: true }).fill("Processed media notifier");
    await drawer.getByLabel("Notification URL").fill("https://processor.example.test/webhooks/deluno");
    await drawer.getByRole("button", { name: "Save callback" }).click();

    await expect(page.getByRole("dialog", { name: "New completion callback" })).toHaveCount(0);
  });

  test("makes external client queue removal an explicit manual setting", async ({ page }) => {
    await authenticateAndNavigate(page, "/indexers/download-clients");

    const setting = page.getByRole("switch", { name: "Allow removing client queue entries" });
    await expect(setting).toHaveAttribute("aria-checked", "false");
    await expect(page.getByText("Remove items from the client queue", { exact: true })).toBeVisible();
    await expect(page.getByText(/A confirmed, manual Remove on Transfers/)).toBeVisible();
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
    await page.getByRole("button", { name: "New list" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New import list" });
    await drawer.getByLabel("Name", { exact: true }).fill("Weekend movies");
    await drawer.getByLabel("Provider").selectOption("url-list");
    await drawer.getByLabel("List URL").fill("https://example.com/weekend-movies.txt");
    await drawer.getByRole("button", { name: "Add list" }).click();
    await expect(page.getByRole("dialog", { name: "Weekend movies" })).toBeVisible();
    await page.getByTitle("Preview without adding titles").click();

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
    await page.getByRole("button", { name: "New list" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New import list" });
    await drawer.getByLabel("Name", { exact: true }).fill("Approved movies");
    await drawer.getByLabel("Provider").selectOption("url-list");
    await drawer.getByLabel("List URL").fill("https://example.com/approved.txt");
    await drawer.getByRole("button", { name: "Add list" }).click();
    await expect(page.getByRole("dialog", { name: "Approved movies" })).toBeVisible();
    await page.getByTitle("Preview without adding titles").click();

    await page.getByRole("checkbox", { name: "Dune (2021)" }).uncheck();
    await page.getByRole("button", { name: "Add selected", exact: true }).click();
    await expect(page.getByText(/1 title added from 1 approved preview entry/)).toBeVisible();
  });

  test("explains sources and downloads as connections", async ({ page }) => {
    await authenticateAndNavigate(page, "/indexers");

    // One toolbar for the whole area: Indexers · Download clients · Library routing.
    const tabs = page.getByRole("navigation", { name: "Sections" });
    await expect(tabs.getByRole("link", { name: "Indexers", exact: true })).toBeVisible();
    await expect(tabs.getByRole("link", { name: "Download clients", exact: true })).toBeVisible();
    await expect(tabs.getByRole("link", { name: "Library routing", exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Indexers", exact: true })).toBeVisible();
  });

  test("gives downloads and imports a clear next step", async ({ page }) => {
    await authenticateAndNavigate(page, "/queue");

    await expect(page.getByRole("heading", { name: "Downloads" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Transfers" })).toBeVisible();
  });

  test("keeps download-health evidence available after queue work", async ({ page }) => {
    await authenticateAndNavigate(page, "/queue");

    await expect(page.getByRole("heading", { name: "Recent activity" })).toBeVisible();
    await expect(page.getByText("Nothing has happened yet")).toBeVisible();
  });

  test("keeps activity as a readable history", async ({ page }) => {
    await authenticateAndNavigate(page, "/activity");

    await expect(page.getByRole("heading", { name: "Job queue" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Sent to downloads" })).toBeVisible();
  });
});

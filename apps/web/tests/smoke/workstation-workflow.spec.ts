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

  test("keeps download paths with the client and library folders simple", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/libraries");

    await expect(page.getByRole("heading", { name: "Finished downloads", exact: true })).toHaveCount(0);
    await page.getByRole("button", { name: "New library", exact: true }).first().click();
    const drawer = page.getByRole("dialog", { name: "New library" });
    await expect(drawer.getByRole("heading", { name: /^Library Profile/ })).toHaveCount(0);
    await expect(drawer.getByRole("heading", { name: /^Automation & Recovery/ })).toHaveCount(0);
    await expect(drawer.getByText("Custom rules", { exact: true })).toHaveCount(0);
    await expect(drawer.getByText("Quality ladder (advanced)", { exact: true })).toHaveCount(0);
    await expect(drawer.getByRole("switch", { name: "Search this library automatically" })).toHaveCount(0);
    await expect(drawer.getByRole("switch", { name: "Find missing media" })).toHaveCount(0);
    await expect(drawer.getByRole("switch", { name: "Look for better releases" })).toHaveCount(0);
    await expect(drawer.getByRole("button", { name: "Choose folder", exact: true })).toHaveCount(1);

    await page.goto("/settings/import-policy");
    await expect(page.getByText("Folder to check after a download finishes", { exact: true })).toHaveCount(0);
  });

  test("falls back to advanced server browsing when the native folder picker is unavailable", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/libraries");
    await page.getByRole("button", { name: "New library", exact: true }).first().click();

    const drawer = page.getByRole("dialog", { name: "New library" });
    await expect(drawer).toBeVisible();
    await page.route("**/api/filesystem/native-folder-picker", async (route) => {
      await route.fulfill({
        status: 409,
        contentType: "application/json",
        body: JSON.stringify({ message: "The native folder picker is unavailable in this session." })
      });
    });

    try {
      await drawer.getByRole("button", { name: "Choose folder", exact: true }).first().click();

      const advancedBrowser = page.getByRole("dialog", { name: "Choose movies library folder" });
      await expect(advancedBrowser).toBeVisible();
      await expect(advancedBrowser.getByText("The Windows picker is unavailable in this session.", { exact: false })).toBeVisible();
      await expect(advancedBrowser.getByText("Server browse", { exact: true })).toBeVisible();
      await expect(advancedBrowser.getByRole("button", { name: "Roots", exact: true })).toBeVisible();
    } finally {
      await page.unroute("**/api/filesystem/native-folder-picker");
    }
  });

  test("applies a folder returned by the native picker", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/libraries");
    await page.getByRole("button", { name: "New library", exact: true }).first().click();

    const drawer = page.getByRole("dialog", { name: "New library" });
    await page.route("**/api/filesystem/native-folder-picker", async (route) => {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ path: "C:\\Media\\Movies", cancelled: false })
      });
    });

    try {
      await drawer.getByRole("button", { name: "Choose folder", exact: true }).first().click();
      await expect(drawer.getByRole("textbox", { name: "Library folder" })).toHaveValue("C:\\Media\\Movies");
    } finally {
      await page.unroute("**/api/filesystem/native-folder-picker");
    }
  });

  test("leaves a new library name blank when switching media type", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/libraries");
    await page.getByRole("button", { name: "New library", exact: true }).first().click();

    const drawer = page.getByRole("dialog", { name: "New library" });
    const name = drawer.getByRole("textbox", { name: "Library name" });
    await expect(name).toHaveValue("");
    await drawer.getByRole("radio", { name: "TV shows", exact: true }).click();
    await expect(name).toHaveValue("");
    await drawer.getByRole("radio", { name: "Movies", exact: true }).click();
    await expect(name).toHaveValue("");
  });

  test("explains unsaved changes before changing a settings menu", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/general");

    await page.getByRole("textbox", { name: "Instance name" }).fill("Unsaved navigation check");
    await expect(page.getByRole("button", { name: "Discard", exact: true })).toHaveCount(0);

    await page.getByRole("navigation", { name: "Sections" }).getByRole("link", { name: "Interface", exact: true }).click();

    const prompt = page.getByRole("dialog", { name: "Unsaved changes" });
    await expect(prompt).toBeVisible();
    await expect(prompt.getByText("Choose how to handle them before leaving.", { exact: false })).toBeVisible();
    await expect(prompt.getByRole("button", { name: /Save and continue/ })).toBeVisible();
    await expect(prompt.getByRole("button", { name: /Discard and continue/ })).toBeVisible();

    await prompt.getByRole("button", { name: /Discard and continue/ }).click();
    await expect(page).toHaveURL(/\/settings\/ui/);
  });

  test("shows real empty-state information instead of invented dashboard activity", async ({ page }) => {
    await authenticateAndNavigate(page, "/");

    // The dashboard states what is actually true of an empty install rather than
    // filling the space with invented activity. Setup guidance is offered, and
    // the library sections say plainly that they are empty.
    await expect(page.getByRole("heading", { name: "Build your media library" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Build my setup" })).toBeVisible();
    const guidedSetup = page.getByRole("region", { name: "Guided setup" });
    const setupProgress = page.getByRole("region", { name: "Setup progress" });
    await expect(guidedSetup).toBeVisible();
    await expect(setupProgress).toBeVisible();
    await expect(page.getByRole("link", { name: /1\. Media Management/ })).toBeVisible();
    const guidedSetupBox = await guidedSetup.boundingBox();
    const setupProgressBox = await setupProgress.boundingBox();
    expect(guidedSetupBox?.y).toBeLessThan(setupProgressBox?.y ?? Number.POSITIVE_INFINITY);
    // Recently added no longer renders while the library is empty: the hero
    // already states it and offers the action, and saying it twice on one
    // screen reads as a bug rather than emphasis (#270, #275). The intent of
    // this test is unchanged — the dashboard states what is true instead of
    // inventing activity — so it now checks where that statement actually is.
    await expect(page.getByRole("heading", { name: "Recently added" })).toHaveCount(0);
    await expect(page.getByText("In your library", { exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: "Add a movie" })).toBeVisible();
  });

  test("makes library display, server-backed order, and refine controls readable", async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");

    await expect(page.getByRole("button", { name: "Update all metadata", exact: true })).toBeVisible();
    // The library picker is Deluno's own listbox rather than a native select —
    // an OS-drawn popup could not match the menus beside it — so it reports the
    // current choice as its label rather than as a form value.
    const libraryPicker = page.getByRole("combobox", { name: "Library", exact: true });
    await expect(libraryPicker).toBeVisible();
    await expect(libraryPicker).toHaveText(/All libraries/);
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

  test("keeps the empty state hidden while the library catalogue is loading", async ({ page }) => {
    await authenticateAndNavigate(page, "/");

    let releaseCatalogueRequest!: () => void;
    const catalogueRequestBlocked = new Promise<void>((resolve) => {
      releaseCatalogueRequest = resolve;
    });
    // The test only means anything while a catalogue request is genuinely held
    // open. Without this signal the run could sail past a request that
    // completed in a millisecond and then fail on the loading assertion for a
    // reason that had nothing to do with the code under test (#271).
    let confirmIntercepted!: () => void;
    const catalogueRequestIntercepted = new Promise<void>((resolve) => {
      confirmIntercepted = resolve;
    });
    await page.route("**/api/movies/page**", async (route) => {
      confirmIntercepted();
      await catalogueRequestBlocked;
      // By the time the block lifts the test has moved on and unrouted, so the
      // route may already be handled or its page navigated away. Neither is a
      // failure — the request only existed to hold the loading state open.
      await route.continue().catch(() => undefined);
    });

    try {
      await page.goto("/movies");
      await catalogueRequestIntercepted;
      await expect(page.getByText("Your movies library is empty", { exact: true })).toHaveCount(0);
      await expect(page.locator('[aria-busy="true"]')).toBeVisible();
    } finally {
      releaseCatalogueRequest();
      await page.unroute("**/api/movies/page**");
    }

    await page.goto("/tv");
    await expect(page.getByRole("button", { name: "Update all metadata", exact: true })).toBeVisible();
  });

  /**
   * The mark on a title says where the title has got to; the half says whether
   * you are monitoring it. Those are two different facts and the poster carries
   * both without one becoming the other — which is the whole reason monitoring
   * is a half rather than a colour of its own (DESIGN-001).
   */
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

          // A title with no file is Missing, and Missing is red — red is freed
          // for it because nothing on a poster is ever a failure or a machine's
          // health. Amber would be claiming a person has to act.
          const markName = monitored ? "Missing" : "Missing · not monitored";

          await page.getByRole("button", { name: /^Display/ }).click();
          await page.getByRole("button", { name: /Medium Balanced/ }).click();
          const mediumMark = page.getByRole("img", { name: markName, exact: true }).first();
          await expect(mediumMark).toBeVisible();
          await expect(mediumMark).not.toHaveClass(/bg-warning/);
          await expect(mediumMark).not.toHaveClass(/bg-success/);
          await expect(titleCard.getByText(monitoringLabel, { exact: true })).toBeVisible();

          // The same mark at every size — it was a chip at medium and a bare dot
          // at small, so the two sizes could disagree about a title.
          await page.getByRole("button", { name: /Small More titles/ }).click();
          const smallMark = page.getByRole("img", { name: markName, exact: true }).first();
          await expect(smallMark).toBeVisible();
          await expect(smallMark).not.toHaveClass(/bg-success/);

          if (monitored) {
            // Solid: monitoring is not deciding anything against this title.
            await expect(smallMark).toHaveClass(/bg-destructive/);
          } else {
            // Half: nothing will go looking for it, said on the dot itself
            // rather than by draining its colour — three drained dots side by
            // side are the same grey.
            await expect(smallMark).toHaveClass(/linear-gradient/);
          }
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

  test("keeps Media Management as a compact sidebar area", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings");

    await expect(page.getByRole("heading", { name: "Setup Overview" })).toBeVisible();
    if (testInfo.project.name === "mobile") {
      await page.getByRole("button", { name: "More destinations" }).click();
      await expect(page.getByLabel("Panel").getByRole("link", { name: "Media Management", exact: true })).toBeVisible();
      return;
    }

    await expect(page.locator("aside").getByRole("navigation", { name: "Media Management" })).toBeVisible();

    // Every configuration area sets `tabsInToolbar`, so the sidebar shows one
    // row per area and the page's own toolbar is how you move between siblings.
    // See the comment on configurationNavAreas: the two must not do the same job
    // twice. Assert the area rows here, and the sibling tabs on the page below.
    const tree = page.locator("aside").getByRole("navigation", { name: "Media Management" });
    for (const destination of ["/settings/libraries"]) {
      await expect(tree.locator(`a[href="${destination}"]`).first()).toHaveCount(1);
    }
    await expect(tree.getByRole("button", { name: /Collapse|Expand/ })).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Set up Deluno in order" })).toBeVisible();
    await expect(page.getByText("Other configuration", { exact: true })).toHaveCount(0);
  });

  test("keeps the same configuration tree in every configuration family", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The mobile drawer has the same destination tree.");
    await authenticateAndNavigate(page, "/settings/media-management");
    await expect(page.getByRole("heading", { name: "Media Management", exact: true })).toBeVisible();
    await expect(page.getByRole("navigation", { name: "Sections" }).getByRole("link", { name: "Media Naming", exact: true })).toHaveAttribute("aria-current", "page");
    await expect(page.getByRole("navigation", { name: "Sections" }).getByRole("link", { name: "Import Policy", exact: true })).toBeVisible();
    await expect(page.getByText("Naming", { exact: true })).toBeVisible();
    await expect(page.getByText("Import Policy", { exact: true })).toHaveCount(1);

    // Area rows, not their children — see the tabsInToolbar rule on
    // configurationNavAreas. Child pages live in the page toolbar.
    const expectedSections = ["/settings/libraries"];
    const navigator = page.locator("aside").getByRole("navigation", { name: "Media Management" });
    await expect(navigator).toBeVisible();
    for (const destination of expectedSections) {
      await expect(navigator.locator(`a[href="${destination}"]`)).toHaveCount(1);
    }

    await page.getByRole("navigation", { name: "Sections" }).getByRole("link", { name: "Library & Storage", exact: true }).click();
    await expect(page.getByText("where Deluno stores and organises your media", { exact: false })).toBeVisible();

    await page.goto("/indexers");
    await expect(navigator.getByRole("link", { name: "Find & Download", exact: true })).toBeVisible();
    await page.goto("/search-cycles");
    await expect(page.getByRole("navigation", { name: "Sections" }).getByRole("link", { name: "Automation", exact: true })).toHaveAttribute("aria-current", "page");
    await expect(page.getByRole("navigation", { name: "Sections" }).getByRole("link", { name: "Missing Searches", exact: true })).toBeVisible();
    await expect(page.locator("aside").getByRole("navigation", { name: "Media Management" }).getByRole("link", { name: "Automation & Recovery", exact: true })).toBeVisible();
    await page.goto("/system");
    await expect(page.locator("aside").getByRole("navigation", { name: "System controls" }).getByRole("link", { name: "System", exact: true })).toBeVisible();
  });

  test("keeps naming choices quiet while making custom patterns discoverable", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/media-management");

    await expect(page.getByText("Live preview", { exact: true })).toBeVisible();
    await expect(page.getByText("See how Deluno will name new and imported media.", { exact: true })).toBeVisible();
    await page.getByRole("radio", { name: "Custom pattern", exact: true }).first().click();
    await expect(page.getByRole("textbox", { name: "Custom pattern", exact: true }).first()).toBeVisible();
    await expect(page.getByRole("button", { name: "Movie title", exact: true }).first()).toBeVisible();

    await page.getByRole("button", { name: "Movie title", exact: true }).first().click();
    await expect(page.getByRole("textbox", { name: "Custom pattern", exact: true }).first()).toHaveValue("{Movie Title}");

    await page.getByRole("button", { name: "Close", exact: true }).click();
    await page.getByRole("navigation", { name: "Sections" }).getByRole("link", { name: "Import Policy", exact: true }).click();
    await expect(page.locator("h1")).toHaveText("Media Management");
    await expect(page.getByRole("switch", { name: "Stop searching once the cutoff is met" })).toHaveCount(0);
  });

  test("keeps installation-wide settings under Maintain Deluno", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name === "mobile", "The desktop sidebar owns this hierarchy test.");
    await authenticateAndNavigate(page, "/settings/general");

    const maintenance = page.locator("aside").getByRole("navigation", { name: "System controls" });
    await expect(maintenance.getByRole("button", { name: /Collapse|Expand/ })).toHaveCount(0);
    await expect(maintenance.getByRole("link", { name: "System", exact: true })).toBeVisible();
    await expect(page.locator("aside").getByRole("navigation", { name: "Media Management" }).getByRole("link", { name: "Preferences", exact: true })).toHaveCount(0);
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

    const sources = page.locator("aside").getByRole("navigation", { name: "Media Management" }).getByRole("link", { name: "Find & Download", exact: true });
    await sources.focus();
    await page.keyboard.press("Enter");

    await expect(page).toHaveURL(/\/indexers/);
    await expect(page.getByRole("heading", { name: "Indexers", exact: true })).toBeVisible();
  });

  test("keeps failed-download handling together with automation and recovery", async ({ page }) => {
    await authenticateAndNavigate(page, "/search-cycles");

    const sections = page.getByRole("navigation", { name: "Sections" });
    await sections.getByRole("link", { name: "Failed Downloads", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Failed downloads" })).toBeVisible();
    await expect(page.getByLabel("Act after this many strikes")).toHaveValue("3");
    await expect(page.getByText("Block this release", { exact: true })).toBeVisible();
    await expect(page.getByText("Search for a replacement", { exact: true })).toBeVisible();
    await expect(page.getByText("Remove the client entry", { exact: true })).toBeVisible();
    await expect(page.getByText("Purge residual files", { exact: true })).toBeVisible();
    await sections.getByRole("link", { name: "Upgrades", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Upgrade Searches", exact: true })).toBeVisible();
    await expect(page.getByRole("switch", { name: "Stop searching once the cutoff is met" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Save automation settings" })).toBeVisible();
  });

  test("keeps navigation compact and explains each destination after it opens", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings");

    if (testInfo.project.name === "mobile") {
      await page.getByRole("button", { name: "More destinations" }).click();
      const drawer = page.getByLabel("Panel");
      await expect(drawer.getByRole("link", { name: "Automation & Recovery", exact: true })).toBeVisible();
      await expect(drawer.getByRole("link", { name: "Media Management", exact: true })).toBeVisible();
      await expect(drawer.getByRole("link", { name: "System", exact: true })).toBeVisible();
      await expect(drawer.getByText("Control room", { exact: true })).toHaveCount(0);
      await drawer.getByRole("link", { name: "Automation & Recovery", exact: true }).click();
      await expect(page.getByText("What Deluno searches for on a schedule, and how it recovers when a download fails", { exact: true })).toBeVisible();
      return;
    }

    await expect(page.locator("aside").getByRole("navigation", { name: "Media dashboard" })).toBeVisible();
    await expect(page.locator("aside").getByRole("navigation", { name: "Live operational status" })).toBeVisible();
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
    await expect(glossary.getByText("Library Profile", { exact: true })).toBeVisible();
    await expect(glossary.getByText("Search Source", { exact: true })).toBeVisible();
    await expect(glossary.getByText("Download Health & Cleanup", { exact: true })).toBeVisible();
    await expect(glossary.getByText("Guide-backed Rules", { exact: true })).toBeVisible();
  });

  test("starts a library profile from an understandable scenario", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/policy-sets");

    // List → drawer: the page contains reusable library profiles.
    await expect(page.getByRole("heading", { name: "Library Profiles", exact: true }).first()).toBeVisible();
    await page.getByRole("button", { name: "New library profile" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New library profile" });
    await expect(drawer).toBeVisible();
    await expect(drawer.getByRole("heading", { name: /^Select libraries to use this profile/ })).toBeVisible();
    await expect(drawer.getByText(/Choose the libraries that should use this profile/)).toBeVisible();
    await expect(drawer.getByLabel("Quality Profile")).toBeVisible();
    await expect(drawer.getByLabel("Release Preferences")).toBeVisible();
    await expect(drawer.getByText("Quality you want", { exact: true })).toHaveCount(0);
    await expect(drawer.getByText("Release choices", { exact: true })).toHaveCount(0);
    await expect(drawer.getByText("Set this profile up in three steps", { exact: true })).toHaveCount(0);
    // #293 — a create form's Save is live from the moment it opens. Nothing
    // here is prefilled, so pressing it has to name what is missing rather than
    // sit inert.
    const create = page.getByRole("button", { name: "Create library profile" });
    await expect(create).toBeEnabled();
    await create.click();
    await expect(drawer.getByText("Give this library profile a name.")).toBeVisible();

    // Leaving with edits asks first.
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog", { name: "New library profile" })).toHaveCount(0);
  });

  test("keeps library profiles limited to selectors for existing release preferences", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/policy-sets");
    await page.getByRole("button", { name: "New library profile" }).first().click();

    const drawer = page.getByRole("dialog", { name: "New library profile" });
    const releaseChoice = drawer.getByLabel("Release Preferences");
    await expect(releaseChoice).toBeVisible();
    await expect(drawer.getByText("What these choices mean", { exact: true })).toHaveCount(0);
    await expect(drawer.getByText("Prefer certain releases", { exact: true })).toHaveCount(0);

    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog", { name: "New library profile" })).toHaveCount(0);
  });

  test("keeps the full release-rule editor behind one advanced section", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings/custom-formats");
    await page.getByRole("button", { name: "New custom rule" }).first().click();

    const drawer = page.getByRole("dialog", { name: "New custom release rule" });
    await expect(drawer.getByLabel("Guide choice")).toBeVisible();
    await expect(drawer.getByLabel("Guide choice").locator("optgroup[label='Release Groups']")).toHaveCount(1);
    await expect(drawer.getByRole("button", { name: "Advanced matching" })).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Add matching criterion" })).toBeVisible();
    await drawer.getByRole("button", { name: "Add matching criterion" }).click();
    await expect(drawer.getByText("Words to match", { exact: true })).toBeVisible();
    await expect(drawer.getByLabel("Match on")).toHaveValue("releaseTitle");
    await expect(drawer.getByLabel("Match on").locator("option[value='releaseGroup']")).toHaveCount(1);

    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);
  });

  test("makes import lists a visible, understandable discovery option", async ({ page }, testInfo) => {
    await authenticateAndNavigate(page, "/settings");

    if (testInfo.project.name === "mobile") await page.getByRole("button", { name: "More destinations" }).click();
    const navigation = testInfo.project.name === "mobile"
      ? page.getByLabel("Panel")
      : page.locator("aside").getByRole("navigation", { name: "Media Management" });
    const discoverMedia = navigation.getByRole("link", { name: "Discover Media", exact: true });
    await expect(discoverMedia).toHaveAttribute("href", "/settings/lists");
    await discoverMedia.click();

    await expect(page.getByRole("heading", { name: "Import Lists", exact: true }).first()).toBeVisible();
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

    // Metadata is a page inside the Media Management area, so it is reached from
    // the page toolbar rather than the sidebar — see the tabsInToolbar rule.
    await page.getByRole("link", { name: "Metadata & Files", exact: true }).first().click();

    await expect(page.getByRole("heading", { name: "Media Management", exact: true }).first()).toBeVisible();
    await expect(page.getByText("Metadata Files", { exact: true })).toBeVisible();
    await expect(page.getByText(/Deluno matches titles and loads posters/)).toHaveCount(0);
    await expect(page.getByText("TMDb API key", { exact: true })).toHaveCount(0);
    await expect(page.getByText("OMDb API key", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Provider route", { exact: true })).toHaveCount(0);

    await page.goto("/system");
    await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
    await expect(page.getByText("This area could not load")).toHaveCount(0);
    await expect(page.getByText("Metadata Maintenance", { exact: true })).toBeVisible();
    await expect(page.getByText("Title matching and library details", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Check now", exact: true })).toBeVisible();
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
    await expect(page.getByRole("heading", { name: "Processor Connections", exact: true })).toBeVisible();
    await expect(page.getByText(/Deluno can watch the processed output folder itself/)).toBeVisible();

    await page.getByRole("button", { name: "Connect processor" }).first().click();
    const drawer = page.getByRole("dialog", { name: "Connect a processor" });
    await drawer.getByLabel("Name", { exact: true }).fill("Processed media notifier");
    await drawer.getByLabel("Processor job URL").fill("https://processor.example.test/webhooks/deluno");
    await drawer.getByRole("button", { name: "Connect processor" }).click();

    await expect(page.getByRole("dialog", { name: "Connect a processor" })).toHaveCount(0);
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
    await expect(tabs.getByRole("link", { name: "Download Clients", exact: true })).toBeVisible();
    await expect(tabs.getByRole("link", { name: "Library Routing", exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Indexers", exact: true })).toBeVisible();

    // The explainer belongs to the area, so it is here on the tab you land on
    // and still here three tabs along. It starts collapsed (#296).
    const explainer = page.getByRole("button", { name: "How this works", exact: true });
    await expect(explainer).toHaveAttribute("aria-expanded", "false");

    await tabs.getByRole("link", { name: "Library Routing", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Library Routing", exact: true })).toBeVisible();
    await expect(explainer).toBeVisible();

    await explainer.click();
    await expect(page.getByText(/Add somewhere to search/)).toBeVisible();
    await expect(page.getByText(/Tell each library which to use/)).toBeVisible();
  });

  test("gives downloads and imports a clear next step", async ({ page }) => {
    await authenticateAndNavigate(page, "/queue");

    await expect(page.getByRole("heading", { name: "Media pipeline" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Transfers" })).toBeVisible();
  });

  test("keeps download-health evidence available after queue work", async ({ page }) => {
    await authenticateAndNavigate(page, "/queue");

    await expect(page.getByRole("heading", { name: "Recent activity" })).toBeVisible();
    await expect(page.getByText("Nothing has happened yet")).toBeVisible();
  });

  test("keeps activity as a readable history", async ({ page }) => {
    await authenticateAndNavigate(page, "/activity");

    await expect(page.getByRole("heading", { name: "Job Queue" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Sent to Downloads" })).toBeVisible();
  });
});

import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate, ensureBootstrapped, fallbackCredentials } from "../helpers/auth-helper";

// The guide saves its draft to the shared smoke backend as fields change, so a
// test that reads the draft must serve its own or it sees another test's values.
async function serveEmptySetupDraft(page: Page) {
  const draft = {
    mode: "simple", mediaIntent: "both", movieRootPath: "", seriesRootPath: "", downloadsPath: "",
    qualityPreset: "", formatGoal: "", indexerName: "", indexerProtocol: "torznab", indexerUrl: "",
    clientName: "", clientProtocol: "qbittorrent", clientHost: "", clientPort: "8080",
    metadataProviderMode: "direct", metadataBrokerUrl: "", backupEnabled: true,
    firstTitleType: "movies", firstTitle: "", firstTitleYear: "", firstTitleMonitored: true,
    updatedUtc: new Date(0).toISOString()
  };
  await page.route("**/api/setup/draft", async (route) => {
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(draft) });
  });
}

test.describe("first-run and auth screens", () => {
  test("setup entry is usable whether the install is fresh or already bootstrapped", async ({ page }) => {
    await page.goto("/setup");

    const setupHeading = page.getByRole("heading", { name: "Set up Deluno" });
    const loginHeading = page.getByRole("heading", { name: "Sign in to Deluno" });

    await expect(setupHeading.or(loginHeading)).toBeVisible();

    if (await setupHeading.isVisible()) {
      await expect(page.getByLabel("Display name")).toBeVisible();
      await expect(page.getByLabel("Username")).toBeVisible();
      await expect(page.getByLabel("Password", { exact: true })).toBeVisible();
      await expect(page.getByRole("button", { name: "Create account" })).toBeDisabled();

      await page.getByLabel("Display name").fill("Test User");
      await page.getByLabel("Username").fill("test-user");
      await page.getByLabel("Password", { exact: true }).fill("password-123");
      await page.getByLabel("Confirm password").fill("different");
      await expect(page.getByRole("button", { name: "Create account" })).toBeEnabled();
    } else {
      await expect(page.getByLabel("Username")).toBeVisible();
      await expect(page.getByRole("button", { name: "Sign in" })).toBeDisabled();
    }
  });

  test("a fresh install can create its first account through the browser", async ({ page }) => {
    await page.goto("/setup");
    const setupHeading = page.getByRole("heading", { name: "Set up Deluno" });

    if (!(await setupHeading.isVisible())) {
      test.skip(true, "Another test has already bootstrapped this shared disposable install.");
    }

    await page.getByLabel("Display name").fill(fallbackCredentials.displayName);
    await page.getByLabel("Username").fill(fallbackCredentials.username);
    await page.getByLabel("Password", { exact: true }).fill(fallbackCredentials.password);
    await page.getByLabel("Confirm password").fill(fallbackCredentials.password);
    await page.getByRole("button", { name: "Create account" }).click();

    await expect(page).not.toHaveURL(/\/setup/);
    await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
  });

  test("login screen exposes the expected sign-in controls", async ({ page }) => {
    // An isolated test install starts unconfigured and intentionally redirects
    // /login to first-run setup. Bootstrap its test-only account before
    // asserting the actual sign-in screen.
    await ensureBootstrapped(page.context().request);
    await page.goto("/login");

    await expect(page.getByRole("heading", { name: "Sign in to Deluno" })).toBeVisible();
    await expect(page.getByLabel("Username")).toBeVisible();
    await expect(page.getByLabel("Password", { exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  });

  test("guided setup resumes a non-secret draft after a refresh", async ({ page }) => {
    await authenticateAndNavigate(page, "/setup-guide");
    await page.getByRole("button", { name: /Folders/ }).click();
    const moviesRoot = page.locator('input[placeholder*="Movies"]');
    const saved = page.waitForResponse(
      (response) => response.url().includes("/api/setup/draft") && response.request().method() === "PUT" && response.request().postData()?.includes("Draft Movies") === true,
      { timeout: 10_000 }
    );
    await moviesRoot.fill("D:\\Media\\Draft Movies");
    await saved; // the draft is saved half a second after the change: wait for the save that carries it

    await page.reload();
    await expect(page.locator('input[placeholder*="Movies"]')).toHaveValue("D:\\Media\\Draft Movies");
  });

  test("guided setup outcomes are keyboard reachable", async ({ page }) => {
    await serveEmptySetupDraft(page);
    await authenticateAndNavigate(page, "/setup-guide");

    const folders = page.getByRole("button", { name: /Folders/ });
    await folders.focus();
    await page.keyboard.press("Enter");

    await expect(page.getByRole("heading", { name: "Folders", exact: true })).toBeVisible();
    await expect(page.locator('input[placeholder*="Movies"]')).toBeVisible();
  });

  test("guided setup explains an incomplete search-source test", async ({ page }) => {
    await serveEmptySetupDraft(page);
    await page.route("**/api/indexers", async (route) => {
      await route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
    });

    await authenticateAndNavigate(page, "/setup-guide");
    await page.getByRole("button", { name: /Find & Download/ }).click();
    await expect(page.getByRole("radiogroup", { name: "Search source presets" })).toBeVisible();
    await page.getByPlaceholder("https://indexer.example/api").fill("");
    await page.getByRole("button", { name: "Test search source" }).click();

    await expect(page.getByText("Enter an indexer URL before testing.")).toBeVisible({ timeout: 10_000 });
  });

  test("guided setup requires an external download app address before testing", async ({ page }) => {
    await serveEmptySetupDraft(page);
    await page.route("**/api/download-clients", async (route) => {
      await route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
    });
    await authenticateAndNavigate(page, "/setup-guide");
    await page.getByRole("button", { name: /Find & Download/ }).click();
    await page.getByRole("button", { name: "Test download client" }).click();
    await expect(page.getByText("Enter a download client host before testing.")).toBeVisible();
    await expect(page.getByPlaceholder("localhost or docker host")).toBeVisible();
  });

  test("guided setup does not mark the baseline ready without tested acquisition services", async ({ page }) => {
    await serveEmptySetupDraft(page);
    await page.route("**/api/indexers", async (route) => {
      await route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
    });
    await page.route("**/api/download-clients", async (route) => {
      await route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
    });
    await authenticateAndNavigate(page, "/setup-guide");
    await page.getByRole("button", { name: /^(?:\d+ )?Start\b/ }).click();

    await expect(page.getByRole("button", { name: "Create baseline" })).toBeDisabled();
    // Untested connections no longer block the baseline (#246) — the guide
    // states plainly that readiness is still outstanding, which is the claim
    // this test exists to pin.
    await expect(
      page.getByText("Deluno cannot be marked operationally ready until a search source is configured and tested.")
    ).toBeVisible();
  });

  test("guided setup shows both stages, Stage 1 before Stage 2 (#1034)", async ({ page }) => {
    await authenticateAndNavigate(page, "/setup-guide");

    await expect(page.getByText("Stage 1", { exact: false })).toBeVisible();
    await expect(page.getByText("This computer and you", { exact: false })).toBeVisible();
    await expect(page.getByText("Stage 2", { exact: false })).toBeVisible();
    await expect(page.getByText("Deluno itself", { exact: false })).toBeVisible();

    // Stage 1's first-run questions all appear in the rail before any media step.
    for (const label of ["Your account", "Time and region", "How you sign in", "Addresses", "Other devices", "Updates"]) {
      await expect(page.getByRole("button", { name: new RegExp(label) })).toBeVisible();
    }
  });

  test("guided setup asks the time and region questions with a detected timezone", async ({ page }) => {
    await authenticateAndNavigate(page, "/setup-guide");
    await page.getByRole("button", { name: /Time and region/ }).click();

    await expect(page.getByRole("heading", { name: "Time and region", exact: true })).toBeVisible();
    await expect(page.getByRole("radiogroup", { name: "Interface language" })).toBeVisible();
    await expect(page.getByLabel("Timezone")).not.toHaveValue("");
    await expect(page.getByRole("button", { name: "Use this browser" })).toBeVisible();
  });

  test("guided setup keeps Start disabled until the Updates question is answered (#936)", async ({ page }) => {
    await authenticateAndNavigate(page, "/setup-guide");
    // The step's badge is its number until an earlier test's saved progress
    // (a fire-and-forget PUT on the shared database) marks it done, when it
    // becomes a tick; the name must not depend on which (#1057).
    await page.getByRole("button", { name: /^(?:\d+ )?Updates\b/ }).click();

    await expect(page.getByRole("heading", { name: "Updates", exact: true })).toBeVisible();
    await expect(page.getByText("Check for updates by itself?", { exact: true })).toBeVisible();
    await expect(page.getByRole("radio", { name: "Check for updates by itself? Yes" })).toHaveAttribute("aria-checked", "false");
    await expect(page.getByRole("radio", { name: "Check for updates by itself? No" })).toHaveAttribute("aria-checked", "false");

    await page.getByRole("button", { name: /^(?:\d+ )?Start\b/ }).click();
    await expect(page.getByRole("button", { name: "Create baseline" })).toBeDisabled();
  });

  test("setup overview shows the complete ordered journey and keeps import lists optional", async ({ page }) => {
    await authenticateAndNavigate(page, "/settings");

    await expect(page.getByRole("heading", { name: "Setup Overview" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Find & Download" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Discover Media" })).toBeVisible();
    await expect(page.getByText(/Optionally configure import lists/)).toBeVisible();
    await expect(page.getByText(/steps complete/)).toBeVisible();

    // "First Acquisition" is deliberately gone. Running a search, download and
    // import is using Deluno, not configuring it, and as a required step it
    // meant setup never finished — a fully configured install still read 4/5
    // and kept a setup banner above every live number on the dashboard.
    await expect(page.getByRole("heading", { name: "First Acquisition" })).toHaveCount(0);
  });
});

import { Page, APIRequestContext, expect } from "@playwright/test";

export const fallbackCredentials = {
  displayName: "E2E Test User",
  username: "e2e-test-user",
  password: "e2e-test-password-123"
};

export async function ensureBootstrapped(request: APIRequestContext) {
  const statusResponse = await request.get("/api/auth/bootstrap-status");
  const status = statusResponse.ok()
    ? ((await statusResponse.json()) as { requiresSetup?: boolean })
    : { requiresSetup: false };

  if (status.requiresSetup) {
    const bootstrap = await request.post("/api/auth/bootstrap", {
      data: fallbackCredentials
    });
    if (bootstrap.ok()) {
      return;
    }
  }

  // Try to login with fallback credentials
  const login = await request.post("/api/auth/login", {
    data: {
      username: fallbackCredentials.username,
      password: fallbackCredentials.password
    }
  });

  if (!login.ok()) {
    // If fallback fails, try env credentials
    if (process.env.DELUNO_E2E_USERNAME && process.env.DELUNO_E2E_PASSWORD) {
      const envLogin = await request.post("/api/auth/login", {
        data: {
          username: process.env.DELUNO_E2E_USERNAME,
          password: process.env.DELUNO_E2E_PASSWORD
        }
      });
      if (!envLogin.ok()) {
        throw new Error("Could not authenticate with env credentials or fallback");
      }
    } else {
      throw new Error("Could not authenticate - no credentials available");
    }
  }
}

export async function authenticateAndNavigate(page: Page, targetUrl: string) {
  // Bootstrap if needed
  await ensureBootstrapped(page.context().request);

  // Navigate to login
  await page.goto("/login");

  // Perform login
  const credentials = process.env.DELUNO_E2E_USERNAME
    ? {
        username: process.env.DELUNO_E2E_USERNAME,
        password: process.env.DELUNO_E2E_PASSWORD!
      }
    : fallbackCredentials;

  await page.getByLabel(/username/i).fill(credentials.username);
  await page.getByLabel("Password", { exact: true }).fill(credentials.password);
  await page.getByRole("button", { name: /sign in/i }).click();

  // Wait for navigation away from login
  await expect(page).not.toHaveURL(/\/login/);

  // Navigate to target
  await page.goto(targetUrl);

  // Verify no errors
  await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
  await expect(page.getByText("This area could not load")).toHaveCount(0);
}

export async function logout(page: Page) {
  // Click user menu or logout button
  const userMenu = page.getByRole("button", { name: /user|account|menu/i }).first();
  if (await userMenu.isVisible()) {
    await userMenu.click();
    const logoutButton = page.getByRole("button", { name: /logout|sign out/i });
    if (await logoutButton.isVisible({ timeout: 2000 })) {
      await logoutButton.click();
      await expect(page).toHaveURL(/\/(login|setup)/);
    }
  }
}

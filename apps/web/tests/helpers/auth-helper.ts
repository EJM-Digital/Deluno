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

/** What the login API answers, and what the app keeps in localStorage. */
interface SignedIn {
  accessToken: string;
  user: unknown;
}

/**
 * Signs in through the API, bootstrapping the test account first when the
 * install is fresh.
 */
async function signInThroughApi(request: APIRequestContext): Promise<SignedIn> {
  await ensureBootstrapped(request);
  const credentials = process.env.DELUNO_E2E_USERNAME
    ? { username: process.env.DELUNO_E2E_USERNAME, password: process.env.DELUNO_E2E_PASSWORD! }
    : fallbackCredentials;
  const login = await request.post("/api/auth/login", { data: credentials });
  if (!login.ok()) {
    throw new Error(`Could not sign in as ${credentials.username}: HTTP ${login.status()}`);
  }

  return (await login.json()) as SignedIn;
}

/**
 * Opens a page as a signed-in user.
 *
 * Signs in through the API and hands the page that session, the way the app
 * keeps one (localStorage), then opens the target directly. It used to sign
 * in through the login form. That cost every spec a cold render of /login,
 * whose loader waits on /api/auth/bootstrap-status before the form exists, and
 * under load that wait outran the test: "Test timeout of 30000ms exceeded,
 * waiting for getByLabel(/username/i)", passing on every re-run. The 90-second
 * wait meant to absorb it never could, because the test's own 30-second budget
 * ended first. None of the specs that use this helper is about signing in; the
 * login form is proved in setup-login.spec.ts and authenticated.spec.ts.
 *
 * The session is written once per tab, marked in sessionStorage (a flag
 * scoped to this test's own tab lifetime, deliberately not moved to
 * localStorage with the real auth keys below), so a spec that signs out
 * stays signed out on its next navigation rather than being signed back in.
 */
export async function authenticateAndNavigate(page: Page, targetUrl: string) {
  const session = await signInThroughApi(page.context().request);
  await page.addInitScript(({ token, user }) => {
    if (sessionStorage.getItem("deluno-e2e-session-seeded")) {
      return;
    }

    localStorage.setItem("deluno-auth-token", token);
    localStorage.setItem("deluno-auth-user", JSON.stringify(user));
    sessionStorage.setItem("deluno-e2e-session-seeded", "1");
  }, { token: session.accessToken, user: session.user });

  await page.goto(targetUrl);
  await expect(page).not.toHaveURL(/\/(login|setup)(\?|$)/);

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

import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const auth = vi.hoisted(() => ({
  bootstrap: vi.fn(),
  login: vi.fn(),
  isAuthenticated: false,
  /** Draws the page again, as the real provider does the moment a sign-in changes who is signed in. */
  rerender: () => undefined as void
}));
vi.mock("../lib/use-auth", async (importOriginal) => {
  const { useState } = await import("react");
  return {
    ...(await importOriginal<object>()),
    useAuth: () => {
      const [, draw] = useState(0);
      auth.rerender = () => draw((count) => count + 1);
      return auth;
    }
  };
});

const { SetupPage } = await import("./setup-page");
const { LoginPage } = await import("./login-page");

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

function renderAt(path: string, entry: string) {
  const router = createMemoryRouter(
    [
      { path, element: path === "/setup" ? <SetupPage /> : <LoginPage /> },
      { path: "*", element: <Where /> }
    ],
    { initialEntries: [entry] }
  );
  return { router, ...render(<RouterProvider router={router} />) };
}

function fillSetup(values: { name?: string; user?: string; password?: string; confirm?: string }) {
  fireEvent.change(screen.getByLabelText("Display name"), { target: { value: values.name ?? "Test User" } });
  fireEvent.change(screen.getByLabelText("Username"), { target: { value: values.user ?? "test-user" } });
  fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: values.password ?? "password-123" } });
  fireEvent.change(screen.getByLabelText("Confirm password"), { target: { value: values.confirm ?? values.password ?? "password-123" } });
}

afterEach(() => {
  cleanup();
  auth.bootstrap.mockReset();
  auth.login.mockReset();
  auth.isAuthenticated = false;
  auth.rerender = () => undefined;
});

describe("first-run account creation", () => {
  it("shows Set up Deluno with its four fields and Create account disabled while any is empty", () => {
    renderAt("/setup", "/setup");

    expect(screen.getByRole("heading", { name: "Set up Deluno" })).toBeTruthy();
    for (const label of ["Display name", "Username"]) expect(screen.getByLabelText(label)).toBeTruthy();
    expect(screen.getByLabelText("Password", { exact: true })).toBeTruthy();
    expect(screen.getByLabelText("Confirm password")).toBeTruthy();
    const create = screen.getByRole("button", { name: "Create account" }) as HTMLButtonElement;
    expect(create.disabled).toBe(true);

    fillSetup({ name: "", confirm: "" });
    expect(create.disabled).toBe(true);
    fillSetup({});
    expect(create.disabled).toBe(false);
  });

  it("refuses a password shorter than 8 characters in plain words and sends nothing", async () => {
    renderAt("/setup", "/setup");
    fillSetup({ password: "short" });

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByText("Use at least 8 characters for the password.")).toBeTruthy();
    expect(auth.bootstrap).not.toHaveBeenCalled();
  });

  it("refuses a confirmation that does not match with Passwords do not match and sends nothing", async () => {
    renderAt("/setup", "/setup");
    fillSetup({ password: "password-123", confirm: "password-124" });

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByText("Passwords do not match.")).toBeTruthy();
    expect(auth.bootstrap).not.toHaveBeenCalled();
  });

  it("creates the account with the trimmed names, then opens the guided setup carrying the return address", async () => {
    auth.bootstrap.mockResolvedValue([]);
    renderAt("/setup", "/setup?return=%2Fmovies");
    fillSetup({ name: "  Test User ", user: " test-user  " });

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/setup-guide?return=%2Fmovies"));
    expect(auth.bootstrap).toHaveBeenCalledExactlyOnceWith("Test User", "test-user", "password-123");
  });

  it("shows the server's reason and stays on the page when the account cannot be made", async () => {
    auth.bootstrap.mockRejectedValue(new Error("Deluno has already been configured."));
    renderAt("/setup", "/setup");
    fillSetup({});

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByText("Deluno has already been configured.")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Set up Deluno" })).toBeTruthy();
  });
});

describe("the sign-in page", () => {
  it("signs the person in and moves them off /login to where they were going", async () => {
    auth.login.mockResolvedValue(undefined);
    renderAt("/login", "/login?return=%2Fseries");
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: " owner " } });
    fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: "Deluno-Test-2026!" } });

    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/series"));
    expect(auth.login).toHaveBeenCalledExactlyOnceWith("owner", "Deluno-Test-2026!", false);
  });

  it("shows the refusal in the card and keeps the person on the page when the credentials are wrong", async () => {
    auth.login.mockRejectedValue(new Error("Wrong username or password."));
    renderAt("/login", "/login");
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "owner" } });
    fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: "not-the-password" } });

    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByText("Wrong username or password.")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Sign in to Deluno" })).toBeTruthy();
    expect(screen.queryByTestId("where")).toBeNull();
  });
});

describe("the recovery codes step of first-run setup (#1392)", () => {
  const codes = Array.from({ length: 10 }, (_, i) => `AAAA-BBBB-CC${String(i).padStart(2, "D")}`);

  it("shows the codes after the account is made and waits until the person says they are saved", async () => {
    auth.bootstrap.mockResolvedValue(codes);
    renderAt("/setup", "/setup?return=%2Fmovies");
    fillSetup({});

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByRole("heading", { name: "Save your recovery codes" })).toBeTruthy();
    expect(screen.getByText("These are the way back into your account if you forget your username or password.")).toBeTruthy();
    expect(screen.getByText("You can make a new set later in Settings, under Account.")).toBeTruthy();
    expect(screen.queryByLabelText("Display name")).toBeNull();
    expect(screen.getAllByRole("listitem")).toHaveLength(10);

    // Signing in must not carry the person on to the guided setup before they have saved the codes.
    auth.isAuthenticated = true;
    const next = screen.getByRole("button", { name: "Continue" }) as HTMLButtonElement;
    expect(next.disabled).toBe(true);
    expect(screen.queryByTestId("where")).toBeNull();

    fireEvent.click(screen.getByLabelText("I have saved these codes"));
    fireEvent.click(next);
    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/setup-guide?return=%2Fmovies"));
  });
});

describe("the sign-in page", () => {
  it("links to the recovery page", () => {
    renderAt("/login", "/login");
    expect(screen.getByRole("link", { name: "Forgot your username or password?" }).getAttribute("href")).toBe("/recover");
  });
});

describe("the recovery codes step of first-run setup, low rows (#1392)", () => {
  const codes = Array.from({ length: 10 }, (_, i) => `AAAA-BBBB-CC${String(i).padStart(2, "D")}`);

  it("stays on the codes when the sign-in lands a moment before them, and the guided setup follows only the person's word", async () => {
    auth.bootstrap.mockImplementation(async () => {
      // Signing in is the first thing the real call does; the page draws again before the codes come back.
      auth.isAuthenticated = true;
      auth.rerender();
      await Promise.resolve();
      return codes;
    });
    const { router } = renderAt("/setup", "/setup");
    fillSetup({});

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    expect(await screen.findByRole("heading", { name: "Save your recovery codes" })).toBeTruthy();
    expect(router.state.location.pathname).toBe("/setup");

    fireEvent.click(screen.getByLabelText("I have saved these codes"));
    fireEvent.click(screen.getByRole("button", { name: "Continue" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/setup-guide"));
    expect(router.state.historyAction).toBe("REPLACE");
  });

  it("replaces the setup page in the history when there are no codes to show", async () => {
    auth.bootstrap.mockResolvedValue([]);
    const { router } = renderAt("/setup", "/setup");
    fillSetup({});

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/setup-guide"));
    expect(router.state.historyAction).toBe("REPLACE");
  });

  it("carries a person who is already signed in straight on, as before, and replaces the page", async () => {
    auth.isAuthenticated = true;
    const { router } = renderAt("/setup", "/setup?return=%2Fmovies");

    await waitFor(() => expect(router.state.location.pathname).toBe("/setup-guide"));
    expect(router.state.historyAction).toBe("REPLACE");
  });

  it("takes a password of exactly 8 characters, and is not submitted by the browser itself", async () => {
    auth.bootstrap.mockResolvedValue(codes);
    renderAt("/setup", "/setup");
    fillSetup({ password: "12345678" });

    expect(fireEvent.submit(screen.getByLabelText("Username").closest("form")!)).toBe(false);

    await screen.findByRole("heading", { name: "Save your recovery codes" });
    expect(auth.bootstrap).toHaveBeenCalledExactlyOnceWith("Test User", "test-user", "12345678");
  });

  it("says in its own words what the page is for before and after the account is made", async () => {
    auth.bootstrap.mockResolvedValue(codes);
    renderAt("/setup", "/setup");
    expect(screen.getByText("Create the first user account for this Deluno install.")).toBeTruthy();
    expect(screen.getByText("After this step, Deluno will require sign-in for every session.")).toBeTruthy();
    fillSetup({});
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));
    await screen.findByRole("heading", { name: "Save your recovery codes" });
    expect(screen.queryByText("Create the first user account for this Deluno install.")).toBeNull();
  });

  it("lets the person try again after the server refuses, and then shows the codes", async () => {
    auth.bootstrap.mockRejectedValueOnce(new Error("Deluno has already been configured.")).mockResolvedValueOnce(codes);
    renderAt("/setup", "/setup");
    fillSetup({});
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByText("Deluno has already been configured.")).toBeTruthy();

    // A refusal leaves the person signed out, so a later sign-in elsewhere still moves them on.
    auth.isAuthenticated = true;
    auth.rerender();
    await waitFor(() => expect(screen.queryByLabelText("Display name")).toBeNull());
  });
});

import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const auth = vi.hoisted(() => ({
  bootstrap: vi.fn(),
  login: vi.fn(),
  isAuthenticated: false
}));
vi.mock("../lib/use-auth", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  useAuth: () => auth
}));

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
  return render(<RouterProvider router={router} />);
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
    auth.bootstrap.mockResolvedValue(undefined);
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

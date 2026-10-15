import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../lib/use-auth";
import { LoginPage } from "./login-page";
import { SetupPage } from "./setup-page";

// Defects found while proving the shell rows of the coverage register (area shell). Each test states what the person
// should get, against the real AuthProvider and a server reply of the shape the real server sends, and is skipped
// with the defect named until it is fixed.

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

function renderPage(path: "/login" | "/setup", entry: string) {
  const router = createMemoryRouter(
    [{ path, element: path === "/login" ? <LoginPage /> : <SetupPage /> }, { path: "*", element: <Where /> }],
    { initialEntries: [entry] }
  );
  return render(<AuthProvider><RouterProvider router={router} /></AuthProvider>);
}

function signInWith(username: string, password: string) {
  fireEvent.change(screen.getByLabelText("Username"), { target: { value: username } });
  fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: password } });
  fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
}

describe("sign-in and first-run refusals read as sentences", () => {
  beforeEach(() => { localStorage.clear(); });
  afterEach(() => { cleanup(); vi.restoreAllMocks(); });

  // DEFECT: LoginPage/login() put the raw response text in the card, so a rate-limited sign-in shows the JSON body.
  it("a rate-limited sign-in (429) shows a sentence, not the JSON body the server sent", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(
      JSON.stringify({ error: "Rate limit exceeded.", retryAfterSeconds: 60 }),
      { status: 429, headers: { "Content-Type": "application/json", "Retry-After": "60" } }));
    renderPage("/login", "/login");

    signInWith("owner", "whatever-it-is");

    await waitFor(() => expect(screen.getByText(/rate limit exceeded/i)).toBeTruthy());
    expect(document.body.textContent).not.toContain('{"error"');
  });

  // DEFECT: SetupPage/bootstrap() put the raw response text in the card, so an install that is already set up shows the JSON body.
  it("an install that is already configured (409) shows a sentence, not the JSON body the server sent", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(
      JSON.stringify({ message: "Deluno has already been configured." }),
      { status: 409, headers: { "Content-Type": "application/json" } }));
    renderPage("/setup", "/setup");
    fireEvent.change(screen.getByLabelText("Display name"), { target: { value: "Test User" } });
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "test-user" } });
    fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: "password-123" } });
    fireEvent.change(screen.getByLabelText("Confirm password"), { target: { value: "password-123" } });

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    await waitFor(() => expect(screen.getByText(/already been configured/i)).toBeTruthy());
    expect(document.body.textContent).not.toContain('{"message"');
  });

  // DEFECT: LoginPage/SetupPage navigate(returnTo) with any `return` value, so return=//other-host lands on /other-host instead of the home page (setupLoader only tests startsWith("/"), which "//other-host" passes).
  it("a return address that names another site is refused and the person lands on the home page", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(
      JSON.stringify({ accessToken: "t", expiresUtc: "2099-01-01T00:00:00Z", user: { id: "1", username: "owner", displayName: "Owner", avatarInitials: "O" } }),
      { status: 200, headers: { "Content-Type": "application/json" } }));
    renderPage("/login", "/login?return=%2F%2Fother-host");

    signInWith("owner", "whatever-it-is");

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/"));
  });

  const signedIn = () => new Response(
    JSON.stringify({ accessToken: "t", expiresUtc: "2099-01-01T00:00:00Z", user: { id: "1", username: "owner", displayName: "Owner", avatarInitials: "O" } }),
    { status: 200, headers: { "Content-Type": "application/json" } });

  it("a wrong password (401, empty body) says so in a sentence", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 401 }));
    renderPage("/login", "/login");
    signInWith("owner", "wrong");
    await waitFor(() => expect(screen.getByText("That username or password is not right.")).toBeTruthy());
  });

  it("a rate-limited sign-in says how long to wait", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(
      JSON.stringify({ error: "Rate limit exceeded.", retryAfterSeconds: 60 }),
      { status: 429, headers: { "Content-Type": "application/json" } }));
    renderPage("/login", "/login");
    signInWith("owner", "x");
    await waitFor(() => expect(screen.getByText(/wait 60 seconds, then sign you in again/i)).toBeTruthy());
  });

  it("a server that cannot be reached is named", async () => {
    vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("Failed to fetch"));
    renderPage("/login", "/login");
    signInWith("owner", "x");
    await waitFor(() => expect(screen.getByText(/could not reach its own API/i)).toBeTruthy());
  });

  it.each([
    ["//other-host"], ["/\\other-host"],["https://other-host/"], ["javascript:alert(1)"], ["/%5Cother-host"],
  ])("sign-in with return=%s lands on the home page, never on another site", async (value) => {
    vi.spyOn(globalThis, "fetch").mockImplementation(async () => signedIn());
    renderPage("/login", `/login?return=${encodeURIComponent(value)}`);
    signInWith("owner", "x");
    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/"));
  });

  it("sign-in with a valid deep link goes there, query string and all", async () => {
    vi.spyOn(globalThis, "fetch").mockImplementation(async () => signedIn());
    renderPage("/login", `/login?return=${encodeURIComponent("/movies?filter=missing")}`);
    signInWith("owner", "x");
    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/movies?filter=missing"));
  });

  it.each([
    ["/movies", "%2Fmovies"],
    ["//other-host", "%2F"],
  ])("creating the account with return=%s opens the guided setup carrying %s", async (asked, carried) => {
    vi.spyOn(globalThis, "fetch").mockImplementation(async () => signedIn());
    renderPage("/setup", `/setup?return=${encodeURIComponent(asked)}`);
    fireEvent.change(screen.getByLabelText("Display name"), { target: { value: "Test User" } });
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "test-user" } });
    fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: "password-123" } });
    fireEvent.change(screen.getByLabelText("Confirm password"), { target: { value: "password-123" } });
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));
    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe(`/setup-guide?return=${carried}`));
  });
});

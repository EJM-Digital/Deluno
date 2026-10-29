import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../lib/use-auth";
import { LoginPage } from "./login-page";
import { SetupPage } from "./setup-page";

// #1329: the first-run account page and the sign-in page, against the real AuthProvider with only the network faked.

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

const signedInReply = () => new Response(
  JSON.stringify({ accessToken: "t", expiresUtc: "2099-01-01T00:00:00Z", user: { id: "1", username: "owner", displayName: "Owner", avatarInitials: "O" } }),
  { status: 200, headers: { "Content-Type": "application/json" } });

function fillSetup(over: Partial<Record<"name" | "user" | "password" | "confirm", string>> = {}) {
  fireEvent.change(screen.getByLabelText("Display name"), { target: { value: over.name ?? "  Test User  " } });
  fireEvent.change(screen.getByLabelText("Username"), { target: { value: over.user ?? "  test-user  " } });
  fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: over.password ?? "password-123" } });
  fireEvent.change(screen.getByLabelText("Confirm password"), { target: { value: over.confirm ?? "password-123" } });
}

beforeEach(() => { localStorage.clear(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); localStorage.clear(); document.title = ""; });

describe("the first-run account page (#1329)", () => {
  it("Show password reveals both password fields and Hide password hides them again", () => {
    renderPage("/setup", "/setup");
    const password = screen.getByLabelText("Password", { exact: true });
    const confirm = screen.getByLabelText("Confirm password");
    expect(password).toHaveAttribute("type", "password");
    expect(confirm).toHaveAttribute("type", "password");

    fireEvent.click(screen.getByRole("button", { name: "Show password" }));
    expect(password).toHaveAttribute("type", "text");
    expect(confirm).toHaveAttribute("type", "text");

    fireEvent.click(screen.getByRole("button", { name: "Hide password" }));
    expect(password).toHaveAttribute("type", "password");
    expect(confirm).toHaveAttribute("type", "password");
  });

  it("display name and username are trimmed before they are sent, and the password is sent exactly as typed", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(signedInReply());
    renderPage("/setup", "/setup");
    fillSetup({ password: " password-123 ", confirm: " password-123 " });

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    await waitFor(() => expect(fetchSpy).toHaveBeenCalled());
    const [url, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/auth/bootstrap");
    expect(JSON.parse(String(init.body))).toMatchObject({ displayName: "Test User", username: "test-user", password: " password-123 " });
  });

  it("while creating, the button shows 'Creating account…' with a spinner and cannot be pressed again", async () => {
    let answer: (response: Response) => void = () => undefined;
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation(() => new Promise<Response>((resolve) => { answer = resolve; }));
    renderPage("/setup", "/setup");
    fillSetup();

    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    const busy = await screen.findByRole("button", { name: "Creating account…" });
    expect(busy).toBeDisabled();
    expect(busy.querySelector("svg.animate-spin")).not.toBeNull();
    fireEvent.click(busy);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    answer(signedInReply());
    await waitFor(() => expect(screen.getByTestId("where").textContent).toMatch(/^\/setup-guide/));
  });

  it("a person already signed in who opens setup is sent on to the guided setup, carrying the return address", async () => {
    localStorage.setItem("deluno-auth-token", "stored-token");
    localStorage.setItem("deluno-auth-user", JSON.stringify({ id: "1", username: "owner", displayName: "Owner", avatarInitials: "O" }));
    localStorage.setItem("deluno-auth-expires", "2099-01-01T00:00:00Z");

    renderPage("/setup", "/setup?return=%2Fmovies");

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/setup-guide?return=%2Fmovies"));
  });
});

describe("the sign-in page's fields (#1329)", () => {
  it("the username field is focused on load, and both fields carry the hints a password manager reads", () => {
    renderPage("/login", "/login");

    const username = screen.getByLabelText("Username");
    expect(document.activeElement).toBe(username);
    expect(username).toHaveAttribute("autocomplete", "username");
    expect(screen.getByLabelText("Password", { exact: true })).toHaveAttribute("autocomplete", "current-password");
  });

  it("pressing Enter in a field submits the form", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(signedInReply());
    renderPage("/login", "/login");
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "owner" } });
    const password = screen.getByLabelText("Password", { exact: true });
    fireEvent.change(password, { target: { value: "secret-pass" } });

    const form = password.closest("form")!;
    fireEvent.submit(form);

    await waitFor(() => expect(fetchSpy).toHaveBeenCalledTimes(1));
    expect(String(fetchSpy.mock.calls[0][0])).toBe("/api/auth/login");
    expect(form.querySelector("button[type=submit]")).not.toBeNull();
  });

  it("the sign-in page keeps the plain tab title 'Deluno', and never asks for the computer's name", async () => {
    document.title = "Deluno";
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify({ hostName: "OFFICE-PC" }), { status: 200 }));
    renderPage("/login", "/login");

    await new Promise((resolve) => setTimeout(resolve, 50)); // reason: let a host-name read that must not happen settle before looking at the title
    expect(document.title).toBe("Deluno");
    expect(fetchSpy.mock.calls.some((call) => String(call[0]).includes("/api/system/info"))).toBe(false);
  });
});

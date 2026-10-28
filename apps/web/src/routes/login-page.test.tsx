import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, describeSessionEnd } from "../lib/use-auth";
import { LoginPage } from "./login-page";

// Sign-in page rows of the shell area (coverage register). These run the real AuthProvider and the real login page
// against a faked server reply; only the network is replaced.

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

function renderLogin(entry: string) {
  const router = createMemoryRouter(
    [{ path: "/login", element: <LoginPage /> }, { path: "*", element: <Where /> }],
    { initialEntries: [entry] }
  );
  return render(<AuthProvider><RouterProvider router={router} /></AuthProvider>);
}

const signedInReply = () => new Response(
  JSON.stringify({ accessToken: "t", expiresUtc: "2099-01-01T00:00:00Z", user: { id: "1", username: "owner", displayName: "Owner", avatarInitials: "O" } }),
  { status: 200, headers: { "Content-Type": "application/json" } });

beforeEach(() => { localStorage.clear(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); localStorage.clear(); });

describe("the sign-in page", () => {
  it("a stored token and user on /login goes straight to the home page", async () => {
    localStorage.setItem("deluno-auth-token", "stored-token");
    localStorage.setItem("deluno-auth-user", JSON.stringify({ id: "1", username: "owner", displayName: "Owner", avatarInitials: "O" }));
    localStorage.setItem("deluno-auth-expires", "2099-01-01T00:00:00Z");

    renderLogin("/login");

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/"));
  });

  it("a signed-in person who opens /login is sent on to the page named in return", async () => {
    localStorage.setItem("deluno-auth-token", "stored-token");
    localStorage.setItem("deluno-auth-user", JSON.stringify({ id: "1", username: "owner", displayName: "Owner", avatarInitials: "O" }));
    localStorage.setItem("deluno-auth-expires", "2099-01-01T00:00:00Z");

    renderLogin(`/login?return=${encodeURIComponent("/series?sort=name")}`);

    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/series?sort=name"));
  });

  it("a validation or server-error reply on sign-in is shown as words, not as the JSON the server sent", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(
      JSON.stringify({ errors: { username: ["Username is required."] } }),
      { status: 400, headers: { "Content-Type": "application/json" } }));
    renderLogin("/login");
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "owner" } });
    fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: "x" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByText("Username is required.")).toBeTruthy();
    expect(document.body.textContent).not.toContain(String.raw`{"error"`);
  });

  it("while signing in the button reads Signing in… with a spinning icon and cannot be pressed again", async () => {
    let finish: (response: Response) => void = () => {};
    vi.spyOn(globalThis, "fetch").mockReturnValue(new Promise<Response>((resolve) => { finish = resolve; }));
    renderLogin("/login");
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "owner" } });
    fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: "x" } });

    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    const busy = await screen.findByRole("button", { name: "Signing in…" });
    expect((busy as HTMLButtonElement).disabled).toBe(true);
    expect(busy.querySelector(".animate-spin")).not.toBeNull();
    await act(async () => { finish(signedInReply()); });
  });

  it("the eye button shows the password as text, then hides it again, and its label follows", () => {
    renderLogin("/login");
    const field = screen.getByLabelText("Password", { exact: true }) as HTMLInputElement;
    expect(field.type).toBe("password");

    fireEvent.click(screen.getByRole("button", { name: "Show password" }));
    expect(field.type).toBe("text");
    expect(screen.getByRole("button", { name: "Hide password" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Hide password" }));
    expect(field.type).toBe("password");
    expect(screen.getByRole("button", { name: "Show password" })).toBeTruthy();
  });

  it("the reason the last session ended is shown above the form, and goes once a refusal replaces it", async () => {
    localStorage.setItem("deluno-signout-reason", describeSessionEnd("2026-06-01T00:00:01Z", false, new Date("2026-06-01T00:00:00Z")));
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(null, { status: 401 }));
    renderLogin("/login");

    const reason = screen.getByRole("status");
    expect(reason.textContent).toMatch(/password changed/);

    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "owner" } });
    fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: "wrong" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByText("That username or password is not right.")).toBeTruthy();
    expect(screen.queryByText(/password changed/)).toBeNull();
  });

  it("the username is trimmed before it is sent, and the password is sent exactly as typed", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(signedInReply());
    renderLogin("/login");
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "  owner  " } });
    fireEvent.change(screen.getByLabelText("Password", { exact: true }), { target: { value: "  spaced pass  " } });

    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const body = JSON.parse(String((fetchMock.mock.calls[0][1] as RequestInit).body));
    expect(body.username).toBe("owner");
    expect(body.password).toBe("  spaced pass  ");
  });
});

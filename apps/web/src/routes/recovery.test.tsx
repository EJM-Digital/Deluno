import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RecoveryCodesManager } from "../components/app/recovery-codes-manager";
import { RecoveryCodesPanel } from "../components/app/recovery-codes-panel";
import { RecoveryCodesPrompt } from "../components/app/recovery-codes-prompt";
import {
  checkRecoveryProof,
  makeRecoveryCodes,
  readRecoveryStatus,
  RecoveryRefusedError,
  resetPasswordWithProof
} from "../lib/recovery";
import { RecoveryPage } from "./recovery-page";
import { LoginPage } from "./login-page";

const CODES = Array.from({ length: 10 }, (_, i) => `AAAA-BBBB-${String(1000 + i).replace(/0/g, "C").replace(/1/g, "D")}`);

type Reply = { status?: number; body?: unknown } | Error;
let replies: Record<string, Reply[]>;
let calls: Array<{ url: string; method: string; body: unknown }>;

function reply(key: string, ...next: Reply[]) {
  replies[key] = next;
}

beforeEach(() => {
  replies = {};
  calls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? "GET";
      calls.push({ url, method, body: init?.body ? JSON.parse(String(init.body)) : undefined });
      const queue = replies[`${method} ${url}`] ?? [];
      const next = queue.length > 1 ? queue.shift()! : queue[0];
      if (!next) return new Response("{}", { status: 404 });
      if (next instanceof Error) throw next;
      return new Response(next.body === undefined ? null : JSON.stringify(next.body), { status: next.status ?? 200 });
    })
  );
  try {
    sessionStorage.clear();
  } catch {
    // noop
  }
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const REFUSED = { status: 400, body: { message: "That did not work. Check it and try again." } };

describe("the recovery calls", () => {
  it("sends a code or a PIN and returns the username, or the server's own sentence when refused", async () => {
    reply("POST /api/auth/recovery/check", { body: { username: "owner" } });
    expect(await checkRecoveryProof({ code: "AAAA-BBBB-CCCC" })).toBe("owner");
    expect(calls.at(-1)?.body).toEqual({ code: "AAAA-BBBB-CCCC" });

    reply("POST /api/auth/recovery/check", REFUSED);
    await expect(checkRecoveryProof({ code: "x" })).rejects.toThrow("That did not work. Check it and try again.");
    reply("POST /api/auth/recovery/check", { status: 429, body: { message: "Too many tries. Wait 10 minutes and try again." } });
    await expect(checkRecoveryProof({ code: "x" })).rejects.toThrow("Too many tries. Wait 10 minutes and try again.");
    reply("POST /api/auth/recovery/check", { status: 502, body: undefined });
    await expect(checkRecoveryProof({ code: "x" })).rejects.toThrow("That did not work. Check it and try again.");
    reply("POST /api/auth/recovery/check", { status: 400, body: { errors: { newPassword: ["Use at least 8 characters for the new password."] } } });
    await expect(checkRecoveryProof({ code: "x" })).rejects.toThrow("Use at least 8 characters for the new password.");
    reply("POST /api/auth/recovery/check", { status: 400, body: {} });
    await expect(checkRecoveryProof({ code: "x" })).rejects.toThrow("That did not work. Check it and try again.");
    reply("POST /api/auth/recovery/check", new TypeError("Failed to fetch"));
    await expect(checkRecoveryProof({ code: "x" })).rejects.toBeInstanceOf(RecoveryRefusedError);
    await expect(checkRecoveryProof({ code: "x" })).rejects.toThrow("Deluno could not be reached. Check that it is running, then try again.");
  });

  it("sets the new password with the proof and reports a refusal in the server's words", async () => {
    reply("POST /api/auth/recovery/reset", { status: 204 });
    await resetPasswordWithProof({ code: "AAAA-BBBB-CCCC" }, "a-new-password");
    expect(calls.at(-1)?.body).toEqual({ code: "AAAA-BBBB-CCCC", newPassword: "a-new-password" });

    reply("POST /api/auth/recovery/reset", REFUSED);
    await expect(resetPasswordWithProof({ code: "1" }, "a-new-password")).rejects.toThrow("That did not work. Check it and try again.");
  });

  it("reads how many codes are left and makes a new set, and says plainly when it cannot", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 7, made: true } });
    expect(await readRecoveryStatus()).toEqual({ remaining: 7, made: true });
    reply("GET /api/auth/recovery-codes", { status: 500, body: {} });
    await expect(readRecoveryStatus()).rejects.toThrow("Could not read your recovery codes.");

    reply("POST /api/auth/recovery-codes", { body: { codes: CODES } });
    expect(await makeRecoveryCodes()).toEqual(CODES);
    reply("POST /api/auth/recovery-codes", { status: 500, body: {} });
    await expect(makeRecoveryCodes()).rejects.toThrow("Could not make new recovery codes.");
  });
});

describe("the recovery page", () => {
  function renderPage() {
    return render(
      <MemoryRouter initialEntries={["/recover"]}>
        <Routes>
          <Route path="/recover" element={<RecoveryPage />} />
          <Route path="/login" element={<p>the sign-in page</p>} />
        </Routes>
      </MemoryRouter>
    );
  }

  it("asks for a recovery code, keeps Check code off until one is typed, and says a fresh install is the only other way", async () => {
    renderPage();

    expect(screen.getByRole("heading", { name: "Get back into Deluno" })).toBeTruthy();
    expect(screen.getByText("Enter a recovery code to find your username and set a new password.")).toBeTruthy();
    const check = screen.getByRole("button", { name: "Check code" }) as HTMLButtonElement;
    expect(check.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "  AAAA-BBBB-CCCC " } });
    expect(check.disabled).toBe(false);
    expect(screen.getByText("If you have lost both your password and your recovery codes, the only way forward is a fresh install.")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Back to sign in" }).getAttribute("href")).toBe("/login");
  });

  it("shows the username for a right code, then sets the new password and says every device was signed out", async () => {
    reply("POST /api/auth/recovery/check", { body: { username: "owner" } });
    reply("POST /api/auth/recovery/reset", { status: 204 });
    renderPage();

    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "AAAA-BBBB-CCCC" } });
    fireEvent.click(screen.getByRole("button", { name: "Check code" }));

    expect(await screen.findByText("owner")).toBeTruthy();
    expect(screen.getByText("Choose a new password.")).toBeTruthy();
    expect(calls.find((call) => call.url === "/api/auth/recovery/check")?.body).toEqual({ code: "AAAA-BBBB-CCCC" });

    const save = screen.getByRole("button", { name: "Set new password" }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("New password"), { target: { value: "a-new-password" } });
    expect(save.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: "a-new-password" } });
    expect(save.disabled).toBe(false);
    fireEvent.click(save);

    expect(await screen.findByText("Your password is changed.")).toBeTruthy();
    expect(screen.getByRole("status").textContent).toContain("every device was signed out");
    expect(calls.find((call) => call.url === "/api/auth/recovery/reset")?.body).toEqual({ code: "AAAA-BBBB-CCCC", newPassword: "a-new-password" });
    expect(screen.getByRole("link", { name: "Go to sign in" }).getAttribute("href")).toBe("/login");
    expect(screen.queryByRole("link", { name: "Back to sign in" })).toBeNull();
  });

  it("refuses a wrong code in the server's words and keeps the person on the same step", async () => {
    reply("POST /api/auth/recovery/check", REFUSED);
    renderPage();

    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "WRONG" } });
    fireEvent.click(screen.getByRole("button", { name: "Check code" }));

    expect((await screen.findByRole("alert")).textContent).toBe("That did not work. Check it and try again.");
    expect(screen.getByLabelText("Recovery code")).toBeTruthy();
    expect(screen.queryByLabelText("New password")).toBeNull();
  });

  it("falls back to a plain sentence when the error is not one of Deluno's", async () => {
    reply("POST /api/auth/recovery/check", { body: { username: "owner" } });
    reply("POST /api/auth/recovery/reset", { status: 500, body: undefined });
    renderPage();
    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "AAAA-BBBB-CCCC" } });
    fireEvent.click(screen.getByRole("button", { name: "Check code" }));
    fireEvent.change(await screen.findByLabelText("New password"), { target: { value: "a-new-password" } });
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: "a-new-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Set new password" }));
    expect((await screen.findByRole("alert")).textContent).toBe("That did not work. Check it and try again.");
  });

  it("refuses a short or mismatched new password before sending anything", async () => {
    reply("POST /api/auth/recovery/check", { body: { username: "owner" } });
    renderPage();
    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "AAAA-BBBB-CCCC" } });
    fireEvent.click(screen.getByRole("button", { name: "Check code" }));
    await screen.findByLabelText("New password");

    fireEvent.change(screen.getByLabelText("New password"), { target: { value: "short" } });
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: "short" } });
    fireEvent.click(screen.getByRole("button", { name: "Set new password" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Use at least 8 characters for the new password.");

    fireEvent.change(screen.getByLabelText("New password"), { target: { value: "a-new-password" } });
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: "a-new-password-2" } });
    fireEvent.click(screen.getByRole("button", { name: "Set new password" }));
    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe("The two passwords do not match."));
    expect(calls.some((call) => call.url === "/api/auth/recovery/reset")).toBe(false);
  });

});

describe("the sign-in page link", () => {
  it("offers Forgot your username or password? and it leads to the recovery page", () => {
    render(
      <MemoryRouter initialEntries={["/login"]}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/recover" element={<p>the recovery page</p>} />
        </Routes>
      </MemoryRouter>
    );

    const link = screen.getByRole("link", { name: "Forgot your username or password?" });
    expect(link.getAttribute("href")).toBe("/recover");
    fireEvent.click(link);
    expect(screen.getByText("the recovery page")).toBeTruthy();
  });
});

describe("the recovery codes panel", () => {
  it("lists the ten codes with the note, and Continue waits for the person to say they are saved", () => {
    const onSaved = vi.fn();
    render(<RecoveryCodesPanel codes={CODES} onSaved={onSaved} />);

    expect(within(screen.getByRole("list", { name: "Recovery codes" })).getAllByRole("listitem")).toHaveLength(10);
    expect(document.body.textContent).toContain("password manager or a printout");
    const next = screen.getByRole("button", { name: "Continue" }) as HTMLButtonElement;
    expect(next.disabled).toBe(true);
    fireEvent.click(next);
    expect(onSaved).not.toHaveBeenCalled();

    fireEvent.click(screen.getByLabelText("I have saved these codes"));
    expect(next.disabled).toBe(false);
    fireEvent.click(next);
    expect(onSaved).toHaveBeenCalledTimes(1);
  });

  it("copies the codes one to a line and says so, or says nothing when the browser refuses", async () => {
    const writeText = vi.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error("denied"));
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    render(<RecoveryCodesPanel codes={CODES} onSaved={() => undefined} doneLabel="Done" />);

    fireEvent.click(screen.getByRole("button", { name: "Copy codes" }));
    expect(await screen.findByRole("button", { name: "Copied" })).toBeTruthy();
    expect(writeText).toHaveBeenCalledWith(CODES.join("\n"));

    fireEvent.click(screen.getByRole("button", { name: "Copied" }));
    expect(await screen.findByRole("button", { name: "Copy codes" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Done" })).toBeTruthy();
  });
});

describe("the recovery codes manager", () => {
  it("says there are none yet, makes the first set without asking, and shows it once", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } }, { body: { remaining: 10, made: true } });
    reply("POST /api/auth/recovery-codes", { body: { codes: CODES } });
    render(<RecoveryCodesManager />);

    expect(await screen.findByText(/You have no recovery codes yet/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Make recovery codes" }));

    expect(await screen.findByRole("list", { name: "Recovery codes" })).toBeTruthy();
    fireEvent.click(screen.getByLabelText("I have saved these codes"));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(await screen.findByText("10 of 10 recovery codes left.")).toBeTruthy();
  });

  it("shows how many are left and asks before a new set voids the old one", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 7, made: true } });
    reply("POST /api/auth/recovery-codes", { body: { codes: CODES } });
    render(<RecoveryCodesManager />);

    expect(await screen.findByText("7 of 10 recovery codes left.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Make new codes" }));
    expect(await screen.findByText("Make new recovery codes?")).toBeTruthy();
    expect(document.body.textContent).toContain("The codes you have now stop working.");
    expect(calls.some((call) => call.method === "POST")).toBe(false);

    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Make new codes" }));
    expect(await screen.findByRole("list", { name: "Recovery codes" })).toBeTruthy();
    expect(calls.filter((call) => call.method === "POST")).toHaveLength(1);
  });

  it("does not make anything when the question is cancelled", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 3, made: true } });
    render(<RecoveryCodesManager />);
    fireEvent.click(await screen.findByRole("button", { name: "Make new codes" }));
    fireEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByText("Make new recovery codes?")).toBeNull());
    expect(calls.some((call) => call.method === "POST")).toBe(false);
  });

  it("says plainly when the codes cannot be read or made", async () => {
    reply("GET /api/auth/recovery-codes", { status: 500, body: {} });
    render(<RecoveryCodesManager />);
    expect((await screen.findByRole("alert")).textContent).toBe("Could not read your recovery codes.");
    expect((screen.getByRole("button", { name: "Make recovery codes" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("says plainly when making the codes fails", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } });
    reply("POST /api/auth/recovery-codes", { status: 500, body: {} });
    render(<RecoveryCodesManager />);
    fireEvent.click(await screen.findByRole("button", { name: "Make recovery codes" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Could not make new recovery codes.");
  });
});

describe("the first sign-in prompt for an older account", () => {
  function renderPrompt(path = "/movies") {
    return render(
      <MemoryRouter initialEntries={[path]}>
        <RecoveryCodesPrompt />
      </MemoryRouter>
    );
  }

  it("asks once an account with no codes has signed in, and Not now holds it until the next sign-in", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } });
    renderPrompt();

    expect(await screen.findByText("Save your recovery codes")).toBeTruthy();
    expect(document.body.textContent).toContain("Your account has no recovery codes yet.");
    fireEvent.click(screen.getByRole("button", { name: "Not now" }));
    await waitFor(() => expect(screen.queryByText("Save your recovery codes")).toBeNull());
    expect(sessionStorage.getItem("deluno-recovery-prompt-dismissed")).toBe("1");

    cleanup();
    renderPrompt();
    await waitFor(() => expect(calls.length).toBeGreaterThan(1));
    expect(screen.queryByText("Save your recovery codes")).toBeNull();
  });

  it("makes the codes from the prompt, shows them once, and is gone for good after they are saved", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } }, { body: { remaining: 10, made: true } });
    reply("POST /api/auth/recovery-codes", { body: { codes: CODES } });
    renderPrompt();

    fireEvent.click(await screen.findByRole("button", { name: "Make recovery codes" }));
    expect(await screen.findByRole("list", { name: "Recovery codes" })).toBeTruthy();
    fireEvent.click(screen.getByLabelText("I have saved these codes"));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    await waitFor(() => expect(screen.queryByText("Save your recovery codes")).toBeNull());
    expect(sessionStorage.getItem("deluno-recovery-prompt-dismissed")).toBeNull();
  });

  it("says plainly when making them fails and leaves the question open", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } });
    reply("POST /api/auth/recovery-codes", { status: 500, body: {} });
    renderPrompt();
    fireEvent.click(await screen.findByRole("button", { name: "Make recovery codes" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Could not make new recovery codes.");
    expect(screen.getByRole("button", { name: "Not now" })).toBeTruthy();
  });

  it("does not ask an account that already has codes, or while the guided setup is open", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 4, made: true } });
    renderPrompt();
    await waitFor(() => expect(calls.length).toBe(1));
    await waitFor(() => expect(screen.queryByText("Save your recovery codes")).toBeNull());
    cleanup();

    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } });
    renderPrompt("/setup-guide?return=%2F");
    await waitFor(() => expect(calls.length).toBe(2));
    expect(screen.queryByText("Save your recovery codes")).toBeNull();
  });

  it("asks again when the browser cannot remember a dismissal", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } });
    const blocked = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    const blockedSet = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    renderPrompt();
    fireEvent.click(await screen.findByRole("button", { name: "Not now" }));
    await waitFor(() => expect(screen.queryByText("Save your recovery codes")).toBeNull());
    blocked.mockRestore();
    blockedSet.mockRestore();
  });
});

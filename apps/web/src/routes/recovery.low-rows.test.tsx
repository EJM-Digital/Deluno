import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RecoveryCodesManager } from "../components/app/recovery-codes-manager";
import { RecoveryCodesPrompt } from "../components/app/recovery-codes-prompt";
import { checkRecoveryProof, makeRecoveryCodes, readRecoveryStatus, RecoveryRefusedError } from "../lib/recovery";
import { RecoveryPage } from "./recovery-page";

// The low rows of the recovery screens (#1392): the states between a request going out and its answer, the exact
// words, and the small guards that the main walk does not reach.

const CODES = Array.from({ length: 10 }, (_, i) => `AAAA-BBBB-CC${String(i).padStart(2, "D")}`);
const REFUSED = { status: 400, body: { message: "That did not work. Check it and try again." } };

/** Answers the password question that stands in front of making recovery codes (#1433). */
async function givePassword() {
  fireEvent.change(await screen.findByLabelText("Current password"), { target: { value: "the-password" } });
  fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
}

type Reply = { status?: number; body?: unknown } | Error;
let replies: Record<string, Reply[]>;

function reply(key: string, ...next: Reply[]) {
  replies[key] = next;
}

function answer(next: Reply | undefined): Response {
  if (!next) return new Response("{}", { status: 404 });
  if (next instanceof Error) throw next;
  return new Response(next.body === undefined ? null : JSON.stringify(next.body), { status: next.status ?? 200 });
}

const releases: Record<string, (response: Response) => void> = {};
const holding = new Set<string>();

function holdNext(key: string) {
  holding.add(key);
  return (status: number, body?: unknown) => {
    releases[key]?.(new Response(body === undefined ? null : JSON.stringify(body), { status }));
  };
}

const sent: Array<{ key: string; body: string | undefined; headers: unknown }> = [];

beforeEach(() => {
  replies = {};
  sent.length = 0;
  holding.clear();
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const key = `${init?.method ?? "GET"} ${String(input)}`;
      sent.push({ key, body: init?.body as string | undefined, headers: init?.headers });
      if (holding.has(key)) {
        holding.delete(key);
        return new Promise<Response>((resolve) => {
          releases[key] = resolve;
        });
      }
      const queue = replies[key] ?? [];
      return answer(queue.length > 1 ? queue.shift() : queue[0]);
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


describe("the recovery calls, low rows", () => {
  it("sends JSON and names its refusal", async () => {
    reply("POST /api/auth/recovery/check", { body: { username: "owner" } });
    await checkRecoveryProof({ code: "AAAA" });
    expect(sent.at(-1)?.headers).toEqual({ "Content-Type": "application/json" });
    expect(new RecoveryRefusedError("x").name).toBe("RecoveryRefusedError");

  });

  it("says plainly that Deluno could not be reached when the signed-in calls never arrive", async () => {
    const words = "Deluno could not be reached. Check that it is running, then try again.";
    reply("GET /api/auth/recovery-codes", new TypeError("Failed to fetch"));
    await expect(readRecoveryStatus()).rejects.toThrow(words);
    reply("POST /api/auth/recovery-codes", new TypeError("Failed to fetch"));
    await expect(makeRecoveryCodes("the-password")).rejects.toThrow(words);
  });
});

describe("the recovery page, low rows", () => {
  function renderPage() {
    return render(
      <MemoryRouter initialEntries={["/recover"]}>
        <Routes>
          <Route path="/recover" element={<RecoveryPage />} />
        </Routes>
      </MemoryRouter>
    );
  }

  async function reachThePasswordStep() {
    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "AAAA-BBBB-CCCC" } });
    fireEvent.click(screen.getByRole("button", { name: "Check code" }));
    await screen.findByText("owner");
  }

  it("sends the code with its spaces trimmed, keeps the button off for blanks, and says Checking while it waits", async () => {
    const release = holdNext("POST /api/auth/recovery/check");
    renderPage();

    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "   " } });
    expect((screen.getByRole("button", { name: "Check code" }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "  AAAA-BBBB-CCCC  " } });
    const form = screen.getByLabelText("Recovery code").closest("form")!;
    expect(fireEvent.submit(form)).toBe(false);
    const checking = (await screen.findByRole("button", { name: "Checking…" })) as HTMLButtonElement;
    expect(checking.disabled).toBe(true);
    release(200, { username: "owner" });
    expect(await screen.findByText("owner")).toBeTruthy();
    expect(sent.some((call) => call.body === JSON.stringify({ code: "AAAA-BBBB-CCCC" }))).toBe(true);
  });

  it("clears an old refusal on the next try and gives the button back after a failure", async () => {
    reply("POST /api/auth/recovery/check", REFUSED);
    renderPage();
    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "WRONG" } });
    fireEvent.click(screen.getByRole("button", { name: "Check code" }));
    await screen.findByRole("alert");
    expect((screen.getByRole("button", { name: "Check code" }) as HTMLButtonElement).disabled).toBe(false);

    const release = holdNext("POST /api/auth/recovery/check");
    fireEvent.click(screen.getByRole("button", { name: "Check code" }));
    await waitFor(() => expect(screen.queryByRole("alert")).toBeNull());
    release(200, { username: "owner" });
    expect(await screen.findByText("owner")).toBeTruthy();
  });

  it("shows only the password step once the code works, with empty password fields, and gates Set new password on both", async () => {
    reply("POST /api/auth/recovery/check", { body: { username: "owner" } });
    renderPage();
    await reachThePasswordStep();

    expect(screen.queryByLabelText("Recovery code")).toBeNull();
    expect((screen.getByLabelText("New password") as HTMLInputElement).value).toBe("");
    expect((screen.getByLabelText("Confirm new password") as HTMLInputElement).value).toBe("");
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: "abcdefgh" } });
    expect((screen.getByRole("button", { name: "Set new password" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("accepts a password of exactly 8 characters, says Saving while it waits, and is not submitted by the browser itself", async () => {
    reply("POST /api/auth/recovery/check", { body: { username: "owner" } });
    renderPage();
    await reachThePasswordStep();

    const release = holdNext("POST /api/auth/recovery/reset");
    fireEvent.change(screen.getByLabelText("New password"), { target: { value: "12345678" } });
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: "12345678" } });
    expect(fireEvent.submit(screen.getByLabelText("New password").closest("form")!)).toBe(false);
    const saving = (await screen.findByRole("button", { name: "Saving…" })) as HTMLButtonElement;
    expect(saving.disabled).toBe(true);
    release(204);
    expect(await screen.findByText("Your password is changed.")).toBeTruthy();
  });

  it("clears an old problem on the next try and gives the button back when the reset is refused", async () => {
    reply("POST /api/auth/recovery/check", { body: { username: "owner" } });
    renderPage();
    await reachThePasswordStep();

    fireEvent.change(screen.getByLabelText("New password"), { target: { value: "short" } });
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: "short" } });
    fireEvent.click(screen.getByRole("button", { name: "Set new password" }));
    await screen.findByRole("alert");

    fireEvent.change(screen.getByLabelText("New password"), { target: { value: "long-enough-1" } });
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: "long-enough-1" } });
    const release = holdNext("POST /api/auth/recovery/reset");
    fireEvent.click(screen.getByRole("button", { name: "Set new password" }));
    await waitFor(() => expect(screen.queryByRole("alert")).toBeNull());
    release(400, { message: "That did not work. Check it and try again." });
    expect((await screen.findByRole("alert")).textContent).toBe("That did not work. Check it and try again.");
    expect((screen.getByRole("button", { name: "Set new password" }) as HTMLButtonElement).disabled).toBe(false);
  });

});

describe("the recovery codes manager, low rows", () => {
  it("keeps Make recovery codes off while the codes are being made, and clears an earlier problem when it starts again", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } });
    reply("POST /api/auth/recovery-codes", { status: 500, body: {} });
    render(<RecoveryCodesManager />);
    fireEvent.click(await screen.findByRole("button", { name: "Make recovery codes" }));
    await givePassword();
    await screen.findByRole("alert");
    expect((screen.getByRole("button", { name: "Make recovery codes" }) as HTMLButtonElement).disabled).toBe(false);

    const release = holdNext("POST /api/auth/recovery-codes");
    fireEvent.click(screen.getByRole("button", { name: "Make recovery codes" }));
    await givePassword();
    await waitFor(() => expect(screen.queryByRole("alert")).toBeNull());
    expect((screen.getByRole("button", { name: "Make recovery codes" }) as HTMLButtonElement).disabled).toBe(true);
    release(200, { codes: CODES });
    expect(await screen.findByRole("list", { name: "Recovery codes" })).toBeTruthy();
  });

  it("does not ask again about new codes after the first new set is saved", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 5, made: true } });
    reply("POST /api/auth/recovery-codes", { body: { codes: CODES } });
    render(<RecoveryCodesManager />);
    fireEvent.click(await screen.findByRole("button", { name: "Make new codes" }));
    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Make new codes" }));
    await givePassword();
    await screen.findByRole("list", { name: "Recovery codes" });
    fireEvent.click(screen.getByLabelText("I have saved these codes"));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    expect(await screen.findByText(/recovery codes left/)).toBeTruthy();
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

describe("the first sign-in prompt, low rows", () => {
  function renderPrompt(path = "/movies") {
    return render(
      <MemoryRouter initialEntries={[path]}>
        <RecoveryCodesPrompt />
      </MemoryRouter>
    );
  }

  it("Escape does nothing while the codes are on screen", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } });
    reply("POST /api/auth/recovery-codes", { body: { codes: CODES } });
    renderPrompt();
    await screen.findByText("Save your recovery codes");
    fireEvent.click(screen.getByRole("button", { name: "Make recovery codes" }));
    await givePassword();
    await screen.findByRole("list", { name: "Recovery codes" });

    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });

    expect(screen.getByRole("list", { name: "Recovery codes" })).toBeTruthy();
    expect(sessionStorage.getItem("deluno-recovery-prompt-dismissed")).toBeNull();
  });

  it("Escape while it asks holds the question until the next sign-in", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } });
    renderPrompt();
    await screen.findByText("Save your recovery codes");

    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });

    await waitFor(() => expect(screen.queryByText("Save your recovery codes")).toBeNull());
    expect(sessionStorage.getItem("deluno-recovery-prompt-dismissed")).toBe("1");
  });

  it("is quiet only on the setup guide itself, not on any address that ends the same", async () => {
    reply("GET /api/auth/recovery-codes", { body: { remaining: 0, made: false } });
    renderPrompt("/movies/setup-guide");
    expect(await screen.findByText("Save your recovery codes")).toBeTruthy();
  });
});

import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { CURRENT_PASSWORD_HEADER, refusalSentence, usePasswordPrompt, withCurrentPassword } from "./password-prompt";

afterEach(cleanup);

function Harness({ onAnswer }: { onAnswer: (answer: string | null) => void }) {
  const { ask, prompt } = usePasswordPrompt();
  return (
    <>
      <button type="button" onClick={() => void ask("Making this change needs your password.").then(onAnswer)}>
        Start
      </button>
      {prompt}
    </>
  );
}

describe("the current password question (#1433)", () => {
  it("is not on screen until a change asks, then says why and waits for a password", async () => {
    render(<Harness onAnswer={() => undefined} />);
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(screen.getByText("Start"));

    expect(await screen.findByRole("dialog")).toBeTruthy();
    expect(screen.getByText("Enter your password")).toBeTruthy();
    expect(screen.getByText("Making this change needs your password.")).toBeTruthy();
    expect((screen.getByLabelText("Current password") as HTMLInputElement).type).toBe("password");
    expect((screen.getByRole("button", { name: "Confirm" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("answers with the password when it is confirmed, by the button or by Enter, and closes", async () => {
    const answers: Array<string | null> = [];
    render(<Harness onAnswer={(answer) => answers.push(answer)} />);

    fireEvent.click(screen.getByText("Start"));
    fireEvent.change(await screen.findByLabelText("Current password"), { target: { value: "first-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
    await waitFor(() => expect(answers).toEqual(["first-password"]));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    fireEvent.click(screen.getByText("Start"));
    const input = await screen.findByLabelText("Current password");
    expect((input as HTMLInputElement).value).toBe("");
    fireEvent.change(input, { target: { value: "second-password" } });
    expect(fireEvent.submit(input.closest("form")!)).toBe(false);
    await waitFor(() => expect(answers).toEqual(["first-password", "second-password"]));
  });

  it("does not answer an empty password", async () => {
    const answers: Array<string | null> = [];
    render(<Harness onAnswer={(answer) => answers.push(answer)} />);
    fireEvent.click(screen.getByText("Start"));

    fireEvent.submit((await screen.findByLabelText("Current password")).closest("form")!);

    expect(answers).toEqual([]);
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  it("answers null when it is cancelled or closed with Escape", async () => {
    const answers: Array<string | null> = [];
    render(<Harness onAnswer={(answer) => answers.push(answer)} />);

    fireEvent.click(screen.getByText("Start"));
    fireEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(answers).toEqual([null]));

    fireEvent.click(screen.getByText("Start"));
    fireEvent.keyDown(await screen.findByRole("dialog"), { key: "Escape" });
    await waitFor(() => expect(answers).toEqual([null, null]));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

describe("the helpers around it", () => {
  it("adds the password to the headers only when there is one", () => {
    expect(withCurrentPassword("secret", { "Content-Type": "application/json" })).toEqual({
      "Content-Type": "application/json",
      [CURRENT_PASSWORD_HEADER]: "secret"
    });
    expect(CURRENT_PASSWORD_HEADER).toBe("X-Current-Password");
    expect(withCurrentPassword(null, { A: "b" })).toEqual({ A: "b" });
    expect(withCurrentPassword(undefined)).toEqual({});
    expect(withCurrentPassword("")).toEqual({});
  });

  it("reads the plain sentence out of a refusal, and falls back when there is none", async () => {
    const reply = (body: unknown, status = 400) => new Response(typeof body === "string" ? body : JSON.stringify(body), { status });
    expect(await refusalSentence(reply({ errors: { currentPassword: ["Current password is not correct."] } }), "x")).toBe("Current password is not correct.");
    expect(await refusalSentence(reply({ errors: { currentPassword: ["Too many tries. Wait 10 minutes and try again."] } }), "x")).toBe("Too many tries. Wait 10 minutes and try again.");
    expect(await refusalSentence(reply({ message: "Said in words." }), "x")).toBe("Said in words.");
    expect(await refusalSentence(reply({}), "fallback")).toBe("fallback");
    expect(await refusalSentence(reply("not json"), "fallback")).toBe("fallback");
  });
});


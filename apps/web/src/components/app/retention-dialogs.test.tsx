import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The three dialogs of Leaving soon (#494, #1008, #1010): Keep, Postpone and Add a title by hand. Each says what it will do, asks for
 * only what it needs, and hands the page one clear choice; the page's own tests (retention-screen.test.tsx) cover what it then sends.
 */

const mocks = vi.hoisted(() => ({ fetchJson: vi.fn(), authedFetch: vi.fn() }));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));

const { RetentionKeepDialog } = await import("./retention-keep-dialog");
const { RetentionPostponeDialog } = await import("./retention-postpone-dialog");
const { RetentionAddTitleDialog } = await import("./retention-add-title-dialog");

beforeEach(() => {
  mocks.fetchJson.mockReset();
  mocks.authedFetch.mockReset();
});
afterEach(() => vi.useRealTimers());

describe("Keep a title", () => {
  const open = (onConfirm = vi.fn(), onOpenChange = vi.fn(), busy = false) => {
    render(<RetentionKeepDialog open onOpenChange={onOpenChange} title="Dementia 13" ruleName="Unwatched films" busy={busy} onConfirm={onConfirm} />);
    return { onConfirm, onOpenChange };
  };

  it("names the title and the rule, and says the keep lasts until it is undone", () => {
    open();
    expect(screen.getByRole("dialog", { name: "Keep Dementia 13?" })).toBeVisible();
    expect(screen.getByText("It leaves Leaving soon now and stays until you undo the keep.")).toBeVisible();
    expect(screen.getByRole("radio", { name: "“Unwatched films”" })).toBeChecked();
  });

  it("keeps it from the rule with the typed note", () => {
    const { onConfirm } = open();
    fireEvent.change(screen.getByPlaceholderText("Kids' favourite"), { target: { value: "Kids' favourite" } });

    fireEvent.click(screen.getByRole("button", { name: "Keep" }));

    expect(onConfirm).toHaveBeenCalledWith({ scope: "rule", note: "Kids' favourite" });
  });

  it("keeps it from every rule when asked, and says no rule will mark it", () => {
    const { onConfirm } = open();

    fireEvent.click(screen.getByRole("radio", { name: "Every rule" }));
    expect(screen.getByText(/No rule will mark it/)).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Keep" }));

    expect(onConfirm).toHaveBeenCalledWith({ scope: "all", note: "" });
  });

  it("Cancel closes it without a choice, and nothing can be pressed while it is busy", () => {
    const { onConfirm, onOpenChange } = open();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it("is fresh each time it opens: the choice and the note are not remembered", () => {
    const { rerender } = render(<RetentionKeepDialog open onOpenChange={vi.fn()} title="A" ruleName="R" onConfirm={vi.fn()} />);
    fireEvent.click(screen.getByRole("radio", { name: "Every rule" }));
    fireEvent.change(screen.getByPlaceholderText("Kids' favourite"), { target: { value: "old note" } });

    rerender(<RetentionKeepDialog open={false} onOpenChange={vi.fn()} title="A" ruleName="R" onConfirm={vi.fn()} />);
    rerender(<RetentionKeepDialog open onOpenChange={vi.fn()} title="A" ruleName="R" onConfirm={vi.fn()} />);

    expect(screen.getByRole("radio", { name: "“R”" })).toBeChecked();
    expect(screen.getByPlaceholderText("Kids' favourite")).toHaveValue("");
  });

  it("while busy neither button can be pressed", () => {
    open(vi.fn(), vi.fn(), true);
    expect(screen.getByRole("button", { name: "Keep" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
  });
});

describe("Postpone a title", () => {
  const open = (onConfirm = vi.fn(), removeAfterUtc: string | null = "2099-10-15T00:00:00Z") => {
    render(<RetentionPostponeDialog open onOpenChange={vi.fn()} title="Dementia 13" removeAfterUtc={removeAfterUtc} onConfirm={onConfirm} />);
    return onConfirm;
  };

  it("starts at 14 days and says the new day before anything is changed", () => {
    open();
    expect(screen.getByRole("dialog", { name: "Postpone Dementia 13?" })).toBeVisible();
    expect(screen.getByLabelText("Days to postpone")).toHaveValue(14);
    expect(screen.getByText(/Its new day is/)).toBeVisible();
  });

  it("sends the days typed", () => {
    const onConfirm = open();
    fireEvent.change(screen.getByLabelText("Days to postpone"), { target: { value: "30" } });

    fireEvent.click(screen.getByRole("button", { name: "Postpone" }));

    expect(onConfirm).toHaveBeenCalledWith(30);
  });

  it.each([["0"], ["-3"], ["2.5"], ["3651"], [""]])("refuses %s days and says what is allowed", (typed) => {
    const onConfirm = open();

    fireEvent.change(screen.getByLabelText("Days to postpone"), { target: { value: typed } });

    expect(screen.getByText("Enter a whole number of days from 1 to 3650.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Postpone" })).toBeDisabled();
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it("counts from today when the title is already past its day", () => {
    vi.useFakeTimers({ now: new Date("2026-10-19T10:00:00Z") });
    open(vi.fn(), "2026-09-01T00:00:00Z");
    fireEvent.change(screen.getByLabelText("Days to postpone"), { target: { value: "1" } });
    // A day after today, not a day after the old day in September.
    expect(screen.getByText(/Its new day is/).textContent).toMatch(/Tomorrow/);
  });
});

describe("Add a title by hand", () => {
  const rules = [
    { id: "r-off", name: "Switched off", level: "movie", isEnabled: false },
    { id: "r1", name: "Unwatched films", level: "movie", isEnabled: true },
    { id: "r2", name: "Old shows", level: "show", isEnabled: true }
  ];

  const found = [
    { entityId: "m1", level: "movie", title: "Dementia 13", year: 1963, alreadyListed: false, kept: false },
    { entityId: "m2", level: "movie", title: "Nosferatu", year: 1922, alreadyListed: true, kept: false },
    { entityId: "m3", level: "movie", title: "Sintel", year: 2010, alreadyListed: false, kept: true }
  ];

  const open = (onAdded = vi.fn(), onOpenChange = vi.fn(), list = rules) => {
    render(<RetentionAddTitleDialog open onOpenChange={onOpenChange} rules={list} onAdded={onAdded} />);
    return { onAdded, onOpenChange };
  };

  const typeAndWait = async (text: string) => {
    fireEvent.change(screen.getByPlaceholderText("Start typing a name"), { target: { value: text } });
    await act(async () => { await vi.advanceTimersByTimeAsync(350); });
  };

  it("offers only the rules that are on, the first one chosen", () => {
    open();
    const select = screen.getByLabelText("Add it to") as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.textContent)).toEqual(["Unwatched films", "Old shows"]);
    expect(select.value).toBe("r1");
  });

  it("says to turn a rule on first when none is on", () => {
    open(vi.fn(), vi.fn(), [rules[0]]);
    expect(screen.getByText("Turn a rule on first. Only a rule that is on has a list.")).toBeVisible();
    expect(screen.queryByPlaceholderText("Start typing a name")).not.toBeInTheDocument();
  });

  it("asks nothing for a single letter and, for two or more, asks once after a 300 ms pause", async () => {
    vi.useFakeTimers();
    mocks.fetchJson.mockResolvedValue(found);
    open();

    fireEvent.change(screen.getByPlaceholderText("Start typing a name"), { target: { value: "d" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(mocks.fetchJson).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText("Start typing a name"), { target: { value: "de" } });
    fireEvent.change(screen.getByPlaceholderText("Start typing a name"), { target: { value: "dem" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(299); });
    expect(mocks.fetchJson).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(5); });

    expect(mocks.fetchJson).toHaveBeenCalledTimes(1);
    expect(mocks.fetchJson).toHaveBeenCalledWith("/api/retention/rules/r1/titles?query=dem");
  });

  it("shows what it found: Add for a title that can join, On the list for one already there, Kept for a kept one", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mocks.fetchJson.mockResolvedValue(found);
    open();

    await typeAndWait("de");

    const rows = screen.getAllByRole("listitem");
    expect(within(rows[0]).getByText("Dementia 13 (1963)")).toBeVisible();
    expect(within(rows[0]).getByRole("button", { name: "Add Dementia 13 to the list" })).toBeVisible();
    expect(within(rows[1]).getByText("On the list")).toBeVisible();
    expect(within(rows[1]).queryByRole("button")).not.toBeInTheDocument();
    expect(within(rows[2]).getByText("Kept")).toBeVisible();
  });

  it("says nothing with files matches when nothing is found", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mocks.fetchJson.mockResolvedValue([]);
    open();

    await typeAndWait("zz");

    expect(screen.getByText("Nothing with files matches that.")).toBeVisible();
  });

  it("Add posts the title to the chosen rule, says it is on the rule by hand, and closes", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mocks.fetchJson.mockResolvedValue(found);
    mocks.authedFetch.mockResolvedValue({ ok: true, status: 200 });
    const { onAdded, onOpenChange } = open();
    await typeAndWait("de");

    fireEvent.click(screen.getByRole("button", { name: "Add Dementia 13 to the list" }));

    await waitFor(() => expect(onAdded).toHaveBeenCalledWith("Dementia 13 is on “Unwatched films” by hand."));
    expect(mocks.authedFetch).toHaveBeenCalledWith("/api/retention/rules/r1/titles", expect.objectContaining({ method: "POST", body: JSON.stringify({ entityId: "m1" }) }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("a refusal shows the server's words and leaves the dialog open", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mocks.fetchJson.mockResolvedValue(found);
    mocks.authedFetch.mockResolvedValue({
      ok: false, status: 400, clone() { return this; },
      json: () => Promise.resolve({ errors: { entityId: ["That title is kept, so a rule cannot list it."] } })
    });
    const { onAdded, onOpenChange } = open();
    await typeAndWait("de");

    fireEvent.click(screen.getByRole("button", { name: "Add Dementia 13 to the list" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("That title is kept, so a rule cannot list it.");
    expect(onAdded).not.toHaveBeenCalled();
    expect(onOpenChange).not.toHaveBeenCalledWith(false);
  });

  it("picking another rule asks that rule's list", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mocks.fetchJson.mockResolvedValue([]);
    open();

    fireEvent.change(screen.getByLabelText("Add it to"), { target: { value: "r2" } });
    await typeAndWait("old");

    expect(mocks.fetchJson).toHaveBeenCalledWith("/api/retention/rules/r2/titles?query=old");
  });
});

import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.fn();
const authedFetchMock = vi.fn();
vi.mock("../../lib/api", () => ({ fetchJson: (...args: unknown[]) => fetchJsonMock(...args) }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const { ConnectionTagsDialog, ConnectionSelectionBar } = await import("./connection-tags-dialog");

const tag = (id: string, name: string) => ({ id, name, color: "slate", description: "", createdUtc: "", updatedUtc: "" });

/** #1044: the same add / remove / replace modes and wording as the title bulk editor. */
describe("ConnectionTagsDialog (#1044)", () => {
  beforeEach(() => {
    fetchJsonMock.mockReset();
    authedFetchMock.mockReset();
    fetchJsonMock.mockImplementation(async (url: string) => (url === "/api/tags" ? [tag("t1", "Kids")] : []));
    authedFetchMock.mockResolvedValue({ ok: true, json: async () => ({ updated: 2 }) });
  });

  function open(onApplied = vi.fn(), onOpenChange = vi.fn()) {
    render(<ConnectionTagsDialog open kind="indexer" noun="indexers" ids={["a", "b"]} onOpenChange={onOpenChange} onApplied={onApplied} />);
    return { onApplied, onOpenChange };
  }

  it("offers Add, Remove and Replace, defaulting to Add, with the title editor's words", () => {
    open();
    const modes = screen.getByRole("combobox", { name: "How to apply the tags" });
    expect([...modes.querySelectorAll("option")].map((item) => item.textContent)).toEqual(["Add", "Remove", "Replace"]);
    expect((modes as HTMLSelectElement).value).toBe("add");
    expect(screen.getByText(/Add keeps existing tags; Remove drops only these; Replace wipes the rest/)).toBeTruthy();
    expect(screen.getByText("Change tags: 2 selected indexers")).toBeTruthy();
  });

  it("needs a tag to add or remove, but Replace with none clears them", async () => {
    open();
    const apply = screen.getByRole("button", { name: "Add tags" }) as HTMLButtonElement;
    expect(apply.disabled).toBe(true);
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "How to apply the tags" }), "replace");
    expect((screen.getByRole("button", { name: "Replace tags" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("sends the picked tags, the mode and the selected ids in one request", async () => {
    const { onApplied, onOpenChange } = open();
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "How to apply the tags" }), "remove");
    await userEvent.click(screen.getByRole("textbox", { name: "Tags to apply" }));
    await userEvent.click(await screen.findByRole("button", { name: "Add Kids to Tags to apply" }));
    await userEvent.click(screen.getByRole("button", { name: "Remove tags" }));

    await waitFor(() => expect(onApplied).toHaveBeenCalled());
    expect(authedFetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = authedFetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/tags/bulk-connections");
    expect(JSON.parse(String(init.body))).toEqual({ kind: "indexer", ids: ["a", "b"], tags: ["Kids"], mode: "remove" });
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("shows the problem and stays open when the change is refused", async () => {
    authedFetchMock.mockResolvedValue({ ok: false, json: async () => ({ errors: { tags: ["Pick at least one tag."] } }) });
    const { onApplied } = open();
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "How to apply the tags" }), "replace");
    await userEvent.click(screen.getByRole("button", { name: "Replace tags" }));
    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(onApplied).not.toHaveBeenCalled();
  });
});

describe("ConnectionSelectionBar (#1044)", () => {
  it("appears only once something is ticked and names its buttons for the kind of connection", async () => {
    const onTags = vi.fn();
    const { rerender } = render(<ConnectionSelectionBar count={0} noun="download clients" onTags={onTags} onClear={vi.fn()} />);
    expect(screen.queryByRole("toolbar")).toBeNull();
    rerender(<ConnectionSelectionBar count={3} noun="download clients" onTags={onTags} onClear={vi.fn()} />);
    expect(screen.getByText("3 selected")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Change tags on the selected download clients" }));
    expect(onTags).toHaveBeenCalled();
  });
});

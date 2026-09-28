import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { MovieCollectionItem } from "../lib/api";
import { tagUserKindLabel } from "../lib/tag-users";

/**
 * #1046: the collection tags row, the "titles that leave every list" setting and
 * the plain names the Tags page uses for the new kinds of tag user.
 */
const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
// The real picker loads the tag list over the network; its own tests cover it.
vi.mock("../components/app/tag-picker", () => ({
  TagPicker: ({ value, onChange, label, placeholder }: { value: string; onChange: (value: string) => void; label: string; placeholder?: string }) => (
    <input aria-label={label} placeholder={placeholder} value={value} onChange={(event) => onChange(event.target.value)} />
  )
}));

const { OffListTagCard } = await import("./settings-lists-page");
const { CollectionTagsRow } = await import("./collections-page");

afterEach(() => authedFetchMock.mockReset());

describe("titles that leave every list (#1046)", () => {
  it("is off and blank until someone turns it on, and refuses to turn on without a tag", () => {
    render(<MemoryRouter><OffListTagCard enabled={false} tag="" onSaved={() => undefined} /></MemoryRouter>);

    expect(screen.getByRole("switch", { name: /^Tag titles that are no longer on any list/ })).not.toBeChecked();
    expect(screen.queryByRole("button", { name: "Save list tag settings" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("switch", { name: /^Tag titles that are no longer on any list/ }));
    expect(screen.getByRole("button", { name: "Save list tag settings" })).toBeDisabled();
    expect(screen.getByText("Choose a tag before turning this on.")).toBeInTheDocument();
  });

  it("saves the switch and the chosen tag together", async () => {
    authedFetchMock.mockResolvedValue({ ok: true });
    const onSaved = vi.fn();
    render(<MemoryRouter><OffListTagCard enabled={false} tag="" onSaved={onSaved} /></MemoryRouter>);

    fireEvent.click(screen.getByRole("switch", { name: /^Tag titles that are no longer on any list/ }));
    fireEvent.change(screen.getByPlaceholderText("For example: off-list"), { target: { value: " off-list " } });
    fireEvent.click(screen.getByRole("button", { name: "Save list tag settings" }));

    await vi.waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    const [url, init] = authedFetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/settings");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(init.body as string)).toEqual({ tagTitlesOffEveryList: true, offListTag: "off-list" });
    await vi.waitFor(() => expect(onSaved).toHaveBeenCalled());
  });
});

describe("collection tags (#1046)", () => {
  const collection = { id: "c1", name: "Nolan Collection", tags: "franchise" } as MovieCollectionItem;

  it("offers Save only once the tags change, and saves the trimmed value", () => {
    const onSave = vi.fn();
    render(<CollectionTagsRow collection={collection} disabled={false} onSave={onSave} />);

    const field = screen.getByLabelText("Tags Nolan Collection gives to movies it adds");
    expect(field).toHaveValue("franchise");
    expect(screen.queryByRole("button", { name: /^Save tags for/ })).not.toBeInTheDocument();

    fireEvent.change(field, { target: { value: " franchise, kids " } });
    fireEvent.click(screen.getByRole("button", { name: "Save tags for Nolan Collection" }));
    expect(onSave).toHaveBeenCalledWith("franchise, kids");
  });
});

describe("Tags page names for the new tag users (#1046)", () => {
  it("says Seerr connection, Collection and List setting", () => {
    expect(tagUserKindLabel("seerrConnection")).toBe("Seerr connection");
    expect(tagUserKindLabel("collection")).toBe("Collection");
    expect(tagUserKindLabel("listSetting")).toBe("List setting");
  });
});

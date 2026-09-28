import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.fn();
const authedFetchMock = vi.fn();
vi.mock("../../lib/api", () => ({ fetchJson: (...args: unknown[]) => fetchJsonMock(...args) }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const { TagPicker, parseTagList, joinTagList } = await import("./tag-picker");

const tag = (id: string, name: string) => ({ id, name, color: "slate", description: "", createdUtc: "", updatedUtc: "" });

function Harness({ initial = "", single = false, warnUnused = true, onValue }: { initial?: string; single?: boolean; warnUnused?: boolean; onValue?: (value: string) => void }) {
  const [value, setValue] = useState(initial);
  return <TagPicker label="Test tags" single={single} warnUnused={warnUnused} value={value} onChange={(next) => { setValue(next); onValue?.(next); }} />;
}

/** #1042: one picker over existing tags, create-new is explicit, unused tags are named in plain words. */
describe("TagPicker (#1042)", () => {
  beforeEach(() => {
    fetchJsonMock.mockReset();
    authedFetchMock.mockReset();
    fetchJsonMock.mockImplementation(async (url: string) => {
      if (url === "/api/tags") return [tag("t1", "Kids"), tag("t2", "Anime")];
      if (url === "/api/tags/usage") return [
        { id: "t1", name: "Kids", movieCount: 2, seriesCount: 1, totalCount: 3, users: [], isUnused: false },
        { id: "t2", name: "Anime", movieCount: 0, seriesCount: 0, totalCount: 0, users: [], isUnused: true }
      ];
      throw new Error(url);
    });
  });

  it("parses and joins the stored comma-separated form without repeats", () => {
    expect(parseTagList("Kids, kids; Anime\n 4k ,")).toEqual(["Kids", "Anime", "4k"]);
    expect(parseTagList(null)).toEqual([]);
    expect(joinTagList(["a", "b"])).toBe("a, b");
  });

  it("picks an existing tag and writes it back as the stored string", async () => {
    const onValue = vi.fn();
    render(<Harness onValue={onValue} />);
    await userEvent.click(screen.getByRole("textbox", { name: "Test tags" }));
    await userEvent.click(await screen.findByRole("button", { name: "Add Kids to Test tags" }));
    expect(onValue).toHaveBeenLastCalledWith("Kids");
    await userEvent.click(screen.getByRole("textbox", { name: "Test tags" }));
    await userEvent.click(await screen.findByRole("button", { name: "Add Anime to Test tags" }));
    expect(onValue).toHaveBeenLastCalledWith("Kids, Anime");
  });

  it("does not create a tag for a typo: Enter picks only an existing tag, creating needs its own button", async () => {
    const onValue = vi.fn();
    render(<Harness onValue={onValue} />);
    const box = screen.getByRole("textbox", { name: "Test tags" });
    await userEvent.type(box, "Kidz{Enter}");
    expect(onValue).not.toHaveBeenCalled();
    expect(authedFetchMock).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: /Create new tag Kidz/ })).toBeTruthy();
  });

  it("creates a new tag on request, then selects it", async () => {
    authedFetchMock.mockResolvedValue({ ok: true, json: async () => tag("t3", "Documentaries") });
    const onValue = vi.fn();
    render(<Harness onValue={onValue} />);
    await userEvent.type(screen.getByRole("textbox", { name: "Test tags" }), "Documentaries");
    await userEvent.click(screen.getByRole("button", { name: /Create new tag Documentaries/ }));
    await waitFor(() => expect(onValue).toHaveBeenLastCalledWith("Documentaries"));
    expect(authedFetchMock).toHaveBeenCalledWith("/api/tags", expect.objectContaining({ method: "POST" }));
  });

  it("marks a tag that no title carries, in plain words, and stays quiet for one that does", async () => {
    render(<Harness initial="Kids, Anime" />);
    const note = await screen.findByRole("status");
    expect(note.textContent).toContain("\"Anime\" is not on any title yet");
    expect(note.textContent).not.toContain("Kids");
  });

  it("does not warn where the picker is putting tags on titles", async () => {
    render(<Harness initial="Anime" warnUnused={false} />);
    await waitFor(() => expect(fetchJsonMock).toHaveBeenCalledWith("/api/tags/usage"));
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("keeps a single-tag picker to one tag and lets it be removed", async () => {
    const onValue = vi.fn();
    render(<Harness single initial="Kids" onValue={onValue} />);
    expect(screen.queryByRole("textbox", { name: "Test tags" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Remove Kids from Test tags" }));
    expect(onValue).toHaveBeenLastCalledWith("");
    expect(screen.getByRole("textbox", { name: "Test tags" })).toBeTruthy();
  });
});
